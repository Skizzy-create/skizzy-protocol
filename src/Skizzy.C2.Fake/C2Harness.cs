using System;
using System.Collections.Generic;
using System.Text;
using Skizzy.C2.Tactical;

namespace Skizzy.C2.Fake
{
    /// <summary>FakeBda + C2Engine stepped together at the C2 tick rate (5 Hz), collecting every C2 report.</summary>
    public sealed class C2Harness
    {
        public readonly FakeBda World;
        public readonly C2Engine Engine;
        public readonly List<C2Event> Events = new List<C2Event>();
        public double Dt = 0.2;
        /// <summary>The track picture engine feeding the C2 engine (M2), or null on the legacy direct path.</summary>
        public readonly TpeWorld Tpe;

        /// <summary>Whether new harnesses feed the engine from the track picture engine (the M2 default).</summary>
        public static bool DefaultTpe = true;

        public C2Harness() : this(DefaultTpe) { }

        public C2Harness(bool tpe)
        {
            World = new FakeBda();
            if (tpe)
            {
                Tpe = new TpeWorld(World, new FakeSensors(World));
                Engine = new C2Engine(Tpe, World);
            }
            else Engine = new C2Engine(World, World);
            World.Sink = Engine;
        }

        /// <summary>One world step, then one engine tick.</summary>
        public void Step()
        {
            World.Step(Dt);
            Engine.Tick();
            Engine.DrainEvents(Events);
        }

        public void Run(double seconds)
        {
            int n = (int)Math.Round(seconds / Dt);
            for (int i = 0; i < n; i++) Step();
        }

        /// <summary>Steps until <paramref name="until"/> holds or <paramref name="maxSeconds"/> pass. Returns whether it held.</summary>
        public bool RunUntil(Func<bool> until, double maxSeconds)
        {
            int n = (int)Math.Round(maxSeconds / Dt);
            for (int i = 0; i < n; i++)
            {
                if (until()) return true;
                Step();
            }
            return until();
        }

        public IssueResult Issue(OrderRequest req)
        {
            var r = Engine.Issue(req);
            Engine.DrainEvents(Events);
            return r;
        }

        public List<C2Event> Of(C2EventKind kind)
        {
            var l = new List<C2Event>();
            foreach (var e in Events) if (e.Kind == kind) l.Add(e);
            return l;
        }

        public bool Logged(C2EventKind kind, string textPart)
        {
            foreach (var e in Events)
                if (e.Kind == kind && e.Text != null && e.Text.IndexOf(textPart, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        public string Log()
        {
            var sb = new StringBuilder();
            foreach (var e in Events) sb.Append(e.Ut.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)).Append(' ').Append(e.Kind).Append(' ').Append(e.Code).Append(": ").AppendLine(e.Text);
            return sb.ToString();
        }
    }
}
