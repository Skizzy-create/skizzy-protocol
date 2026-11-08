using System;
using System.Collections.Generic;
using System.Threading;
using Skizzy.Protocol.Errors;
using Skizzy.Protocol.Json;

namespace Skizzy.Host.Core
{
    /// <summary>
    /// A long-running operation (03 §3 <c>op.*</c>). Owned by the host, not the socket: it keeps running if the client
    /// disconnects. Stepped on the main thread once per frame through <see cref="Steps"/> (yield null = next frame).
    /// </summary>
    public sealed class Operation
    {
        public string Id;
        public string Capability;
        public string SessionId;
        public string ActorId;
        public string State = "running";   // running | done | failed | cancelled
        public JsonValue Result;
        public SkizzyError Error;
        public double Progress;
        public string ProgressText;
        public IEnumerator<object> Steps;
        public volatile bool CancelRequested;
        public long StartedMs = Clock.NowMs;
        public long EndedMs;
        internal readonly List<Action<Operation>> Waiters = new List<Action<Operation>>();

        public bool Finished => State != "running";

        public JsonValue ToJson(bool withResult)
        {
            var o = JsonValue.Obj().Set("op", Id).Set("capability", Capability).Set("state", State)
                .Set("progress", Math.Round(Progress, 3)).Set("elapsedS", Math.Round(((EndedMs > 0 ? EndedMs : Clock.NowMs) - StartedMs) / 1000.0, 2));
            if (ProgressText != null) o.Set("status", ProgressText);
            if (withResult && Result != null) o.Set("result", Result);
            if (Error != null) o.Set("error", Error.ToErrorObject());
            return o;
        }
    }

    public sealed class OpTable
    {
        private readonly List<Operation> _ops = new List<Operation>();
        private readonly IHostLog _log;
        private int _next;

        public OpTable(IHostLog log) { _log = log; }

        public int RunningCount
        {
            get
            {
                lock (_ops)
                {
                    int n = 0;
                    foreach (var o in _ops) if (!o.Finished) n++;
                    return n;
                }
            }
        }

        public Operation Start(string capability, Session s, IEnumerator<object> steps)
        {
            var op = new Operation
            {
                Id = "op:" + Interlocked.Increment(ref _next),
                Capability = capability,
                SessionId = s != null ? s.Id : null,
                ActorId = s != null ? s.ActorId : null,
                Steps = steps,
            };
            lock (_ops)
            {
                _ops.Add(op);
                if (_ops.Count > 200) PruneLocked();
            }
            return op;
        }

        private void PruneLocked()
        {
            for (int i = 0; i < _ops.Count && _ops.Count > 100; i++)
                if (_ops[i].Finished) { _ops.RemoveAt(i); i--; }
        }

        public Operation Find(string id)
        {
            lock (_ops)
                foreach (var o in _ops) if (o.Id == id) return o;
            return null;
        }

        public void List(List<Operation> into) { lock (_ops) into.AddRange(_ops); }

        /// <summary>Main thread: advances each running op by one step.</summary>
        public void Step()
        {
            Operation[] snapshot;
            lock (_ops)
            {
                if (_ops.Count == 0) return;
                snapshot = _ops.ToArray();
            }
            foreach (var op in snapshot)
            {
                if (op.Finished) continue;
                if (op.CancelRequested) { Finish(op, "cancelled", null, SkizzyError.Cancelled("client", "operation cancelled")); continue; }
                try
                {
                    if (!op.Steps.MoveNext())
                    {
                        if (op.Error != null) Finish(op, "failed", null, op.Error);
                        else Finish(op, "done", op.Result ?? JsonValue.Obj(), null);
                    }
                }
                catch (SkizzyError e) { Finish(op, "failed", null, e); }
                catch (Exception e)
                {
                    _log.Error("operation " + op.Id + " (" + op.Capability + ") failed: " + e);
                    Finish(op, "failed", null, new SkizzyError(ErrorKind.ProviderError, e.GetType().Name + ": " + e.Message, null, null,
                        JsonValue.Obj().Set("provider", op.Capability).Set("exceptionType", e.GetType().FullName)));
                }
            }
        }

        /// <summary>Cancels every running op (kill switch, scene change, host stop).</summary>
        public void CancelAll(string reason)
        {
            Operation[] snapshot;
            lock (_ops) snapshot = _ops.ToArray();
            foreach (var op in snapshot)
                if (!op.Finished) Finish(op, "cancelled", null, SkizzyError.Cancelled(reason, "operation cancelled: " + reason));
        }

        public void Finish(Operation op, string state, JsonValue result, SkizzyError error)
        {
            List<Action<Operation>> waiters;
            lock (_ops)
            {
                if (op.Finished) return;
                op.State = state;
                op.Result = result;
                op.Error = error;
                op.EndedMs = Clock.NowMs;
                if (state == "done") op.Progress = 1;
                waiters = new List<Action<Operation>>(op.Waiters);
                op.Waiters.Clear();
            }
            try { (op.Steps as IDisposable)?.Dispose(); } catch (Exception) { }
            foreach (var w in waiters)
            {
                try { w(op); } catch (Exception e) { _log.Error("op waiter failed: " + e.Message); }
            }
        }

        /// <summary>Calls <paramref name="done"/> when the op finishes or after the timeout (with the op still running).</summary>
        public void Wait(Operation op, int timeoutMs, Action<Operation> done)
        {
            int fired = 0;
            Timer timer = null;
            Action<Operation> once = o =>
            {
                if (Interlocked.Exchange(ref fired, 1) != 0) return;
                timer?.Dispose();
                done(o);
            };
            lock (_ops)
            {
                if (!op.Finished)
                {
                    op.Waiters.Add(once);
                    timer = new Timer(_ =>
                    {
                        lock (_ops) op.Waiters.Remove(once);
                        once(op);
                    }, null, Math.Max(0, timeoutMs), Timeout.Infinite);
                    return;
                }
            }
            once(op);
        }
    }

    /// <summary>
    /// Kill switch stub (17 M0, PLAN §6.2): denies non-read calls, cancels ops and pending confirmations, shows a
    /// banner. Release only from the in-game UI or a human actor (03 §3).
    /// </summary>
    public sealed class KillSwitch
    {
        private readonly object _gate = new object();
        public bool Engaged { get; private set; }
        public string Since { get; private set; }
        public double SinceUt { get; private set; }
        public string By { get; private set; }
        public string Reason { get; private set; }

        public bool Engage(string by, string reason, double ut)
        {
            lock (_gate)
            {
                if (Engaged) return false;
                Engaged = true;
                Since = Clock.WallUtcIso();
                SinceUt = ut;
                By = by;
                Reason = reason;
                return true;
            }
        }

        public bool Release()
        {
            lock (_gate)
            {
                if (!Engaged) return false;
                Engaged = false;
                return true;
            }
        }

        public JsonValue ToJson()
        {
            lock (_gate)
                return JsonValue.Obj().Set("engaged", Engaged).Set("since", Engaged ? Since : null).Set("by", Engaged ? By : null)
                    .Set("reason", Engaged ? Reason : null);
        }
    }

    /// <summary>Capability fault tracker (02 §5): 5 faults in 60 s auto-disables the capability.</summary>
    public sealed class FaultTracker
    {
        private readonly Dictionary<string, List<long>> _faults = new Dictionary<string, List<long>>();
        private readonly HashSet<string> _disabled = new HashSet<string>();
        public int Threshold = 5;
        public long WindowMs = 60000;

        /// <summary>Records a fault; returns true if this fault disabled the capability.</summary>
        public bool Record(string capability)
        {
            lock (_faults)
            {
                if (!_faults.TryGetValue(capability, out var list)) _faults[capability] = list = new List<long>();
                long now = Clock.NowMs;
                list.Add(now);
                list.RemoveAll(t => now - t > WindowMs);
                if (list.Count >= Threshold && _disabled.Add(capability)) return true;
                return false;
            }
        }

        public bool IsDisabled(string capability) { lock (_faults) return _disabled.Count > 0 && _disabled.Contains(capability); }

        public void Enable(string capability) { lock (_faults) { _disabled.Remove(capability); _faults.Remove(capability); } }

        public List<string> Disabled() { lock (_faults) return new List<string>(_disabled); }
    }
}
