using System;

namespace Skizzy.C2
{
    public enum C2EventKind
    {
        OrderAccepted = 0,
        OrderRefused,
        OrderProgress,
        OrderAchieved,
        OrderFailed,
        OrderCancelled,
        OrderSuperseded,
        OrderSuspended,
        OrderResumed,
        /// <summary>Self-defence interrupted an order (23 §6 point 10 rule 2: logged).</summary>
        Interruption,
        /// <summary>The interruption or diversion ended and the unit is back on its order.</summary>
        BackOnTask,
        /// <summary>Engage on contact: the unit diverted to a contact its own radar holds (row 6).</summary>
        Diversion,
        /// <summary>New contact facts: first identification, own-sensor-only (stealth) contacts (rows 7, 24).</summary>
        Contact,
        /// <summary>First launch by a side in this session (row 27). <see cref="C2Event.PauseRequested"/> per doctrine.</summary>
        FirstLaunch,
        /// <summary>Advance on contact started for a side (row 28).</summary>
        AdvanceStarted,
        /// <summary>Patrol engage/resume and other executor facts worth a log line.</summary>
        Info,
    }

    /// <summary>
    /// One report for the message log, the reports engine and agents (09 §1.3, 23 §6 point 10 rule 3). The host
    /// turns these into <c>order.*</c> events and team-channel reports.
    /// </summary>
    public sealed class C2Event
    {
        public double Ut;
        public C2EventKind Kind;
        /// <summary>Message-log category: orders | contact | weapons | c2.</summary>
        public string Category;
        public string Side;
        /// <summary>Order id (0 = none).</summary>
        public int Order;
        /// <summary>Unit (0 = order-level or side-level event).</summary>
        public uint Unit;
        /// <summary>Refusal code (wire form) or terminal reason.</summary>
        public string Code;
        public string Text;
        public bool PauseRequested;

        public override string ToString() { return Kind + " " + (Text ?? ""); }
    }
}
