using System;
using System.Collections.Concurrent;
using System.Threading;
using Skizzy.Host.Core.World;

namespace Skizzy.Host.Core
{
    /// <summary>A main-thread work item: a capability call or an internal action.</summary>
    public sealed class Job
    {
        public CallContext Call;
        public Action Action;
        public long EnqueuedTicks;
    }

    /// <summary>
    /// Main-thread queues with an adaptive budget (02 §2.2: 0.5–8 ms, default 2 ms) and idle wake (02 §2).
    /// When nothing is queued and nothing is active, the pump MonoBehaviour is disabled; enqueueing posts one wake
    /// callback through <see cref="IMainThreadWaker"/>. The awake flag protocol (store then re-check on the idle side,
    /// enqueue then CAS on the producer side) guarantees no lost wake-ups.
    /// </summary>
    public sealed class Dispatcher
    {
        private readonly ConcurrentQueue<Job> _main = new ConcurrentQueue<Job>();
        private readonly ConcurrentQueue<Job> _fixed = new ConcurrentQueue<Job>();
        private readonly IMainThreadWaker _waker;
        private int _awake;
        private int _backlogFrames;
        public double BaseBudgetMs;
        public double MinBudgetMs;
        public double MaxBudgetMs;
        public double BudgetMs;
        public long Executed;
        public long Wakes;
        public readonly TimingWindow DrainMs = new TimingWindow();
        public readonly TimingWindow QueueWaitMs = new TimingWindow();
        public Action<Job> Execute;

        public Dispatcher(IMainThreadWaker waker, double baseMs, double minMs, double maxMs)
        {
            _waker = waker;
            BaseBudgetMs = BudgetMs = baseMs;
            MinBudgetMs = minMs;
            MaxBudgetMs = maxMs;
        }

        public int MainCount => _main.Count;
        public int FixedCount => _fixed.Count;
        public bool HasWork => !_main.IsEmpty || !_fixed.IsEmpty;
        public bool Awake => Volatile.Read(ref _awake) != 0;

        public void EnqueueMain(Job j)
        {
            j.EnqueuedTicks = Clock.Ticks;
            _main.Enqueue(j);
            Wake();
        }

        public void EnqueueFixed(Job j)
        {
            j.EnqueuedTicks = Clock.Ticks;
            _fixed.Enqueue(j);
            Wake();
        }

        /// <summary>Any thread: makes sure the pump runs next frame.</summary>
        public void Wake()
        {
            if (Interlocked.CompareExchange(ref _awake, 1, 0) == 0)
            {
                Interlocked.Increment(ref Wakes);
                _waker.Wake();
            }
        }

        /// <summary>Main thread, from the pump when it decides to go idle. Returns false if work raced in.</summary>
        public bool TrySleep(Func<bool> stillWanted)
        {
            Interlocked.Exchange(ref _awake, 0);
            if (HasWork || stillWanted())
            {
                Interlocked.Exchange(ref _awake, 1);
                return false;
            }
            return true;
        }

        /// <summary>Main thread: marks the pump as running (called when it is re-enabled).</summary>
        public void MarkAwake() { Interlocked.Exchange(ref _awake, 1); }

        // skizzy:hot Dispatcher.DrainMain
        public void DrainMain()
        {
            if (_main.IsEmpty) { Decay(); return; }
            long start = Clock.Ticks;
            double budgetTicks = BudgetMs / Clock.ToMs(1);
            int n = 0;
            while (_main.TryDequeue(out var j))
            {
                QueueWaitMs.Add(Clock.SinceMs(j.EnqueuedTicks));
                Execute(j);
                n++;
                if (Clock.Ticks - start > budgetTicks) break;
            }
            Executed += n;
            DrainMs.Add(Clock.SinceMs(start));
            if (!_main.IsEmpty)
            {
                if (++_backlogFrames >= 3)
                {
                    BudgetMs = Math.Min(MaxBudgetMs, BudgetMs * 1.5);
                    _backlogFrames = 0;
                }
            }
            else Decay();
        }

        public void DrainFixed()
        {
            if (_fixed.IsEmpty) return;
            long start = Clock.Ticks;
            double budgetTicks = BudgetMs / Clock.ToMs(1);
            while (_fixed.TryDequeue(out var j))
            {
                Execute(j);
                Executed++;
                if (Clock.Ticks - start > budgetTicks) break;
            }
        }

        private void Decay()
        {
            _backlogFrames = 0;
            if (BudgetMs > BaseBudgetMs) BudgetMs = Math.Max(BaseBudgetMs, BudgetMs * 0.9);
        }

        /// <summary>Frame-time pressure: shrink the budget when frames are slow (called by the pump).</summary>
        public void NoteFrameMs(double frameMs)
        {
            if (frameMs > 33 && BudgetMs > MinBudgetMs) BudgetMs = Math.Max(MinBudgetMs, BudgetMs * 0.8);
        }
    }
}
