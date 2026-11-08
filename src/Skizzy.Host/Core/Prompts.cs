using System;
using System.Collections.Generic;
using System.Threading;
using Skizzy.Protocol.Catalog;

namespace Skizzy.Host.Core
{
    public enum PromptAnswer { AllowOnce, AllowSession, AllowAlways, Deny, DenyAndPause, Timeout, Cancelled }

    /// <summary>
    /// An in-game confirmation (PLAN §6.6): client name, capability title and level, a readable argument summary and
    /// the AI's reason labelled as AI-supplied. The call is a continuation, never a blocked thread.
    /// </summary>
    public sealed class PromptRequest
    {
        public string Id;
        public string SessionId;
        public string ClientName;
        public string ActorId;
        public string Role;
        public CapabilityDef Def;
        public string Capability;
        public string Summary;
        public string AiReason;
        public double TimeoutS;
        public long CreatedMs = Clock.NowMs;
        public bool IsGrantRequest;   // permissions.request
        internal Action<PromptAnswer> Answer;
        internal Timer Timer;
        internal int Answered;

        public double RemainingS => Math.Max(0, TimeoutS - (Clock.NowMs - CreatedMs) / 1000.0);
    }

    /// <summary>Pending prompts. The in-game UI renders <see cref="Snapshot"/> and calls <see cref="Answer"/>.</summary>
    public sealed class PromptQueue
    {
        private readonly List<PromptRequest> _pending = new List<PromptRequest>();
        private int _next;
        /// <summary>Headless hosts (FakeHost, tests) answer automatically; null = wait for a human.</summary>
        public PromptAnswer? AutoAnswer;
        public Action Changed;

        public int Count { get { lock (_pending) return _pending.Count; } }

        public void Snapshot(List<PromptRequest> into) { lock (_pending) into.AddRange(_pending); }

        public void Show(PromptRequest r, Action<PromptAnswer> answer)
        {
            r.Id = "confirm:" + Interlocked.Increment(ref _next);
            r.Answer = answer;
            if (AutoAnswer.HasValue)
            {
                var a = AutoAnswer.Value;
                ThreadPool.QueueUserWorkItem(_ => Answer(r, a));
                lock (_pending) _pending.Add(r);
                return;
            }
            lock (_pending) _pending.Add(r);
            r.Timer = new Timer(_ => Answer(r, PromptAnswer.Timeout), null, (int)(r.TimeoutS * 1000), Timeout.Infinite);
            try { Changed?.Invoke(); } catch (Exception) { }
        }

        public bool Answer(string id, PromptAnswer a)
        {
            PromptRequest r = null;
            lock (_pending)
                foreach (var p in _pending) if (p.Id == id) { r = p; break; }
            if (r == null) return false;
            Answer(r, a);
            return true;
        }

        public void Answer(PromptRequest r, PromptAnswer a)
        {
            if (Interlocked.Exchange(ref r.Answered, 1) != 0) return;
            lock (_pending) _pending.Remove(r);
            r.Timer?.Dispose();
            try { r.Answer(a); } catch (Exception) { }
            try { Changed?.Invoke(); } catch (Exception) { }
        }

        /// <summary>Rejects every pending prompt (kill switch) or those of one session (disconnect).</summary>
        public void CancelAll(string sessionId = null)
        {
            var copy = new List<PromptRequest>();
            lock (_pending)
                foreach (var p in _pending) if (sessionId == null || p.SessionId == sessionId) copy.Add(p);
            foreach (var p in copy) Answer(p, PromptAnswer.Cancelled);
        }
    }
}
