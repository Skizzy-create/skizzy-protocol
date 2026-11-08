using System;
using System.Collections.Generic;
using System.Globalization;

namespace Skizzy.C2
{
    /// <summary>Phase v1 order types (09 §2.1, 24 rows 3-14, 21-28).</summary>
    public enum OrderType
    {
        MoveTo = 0,
        MoveRoute,
        Engage,
        HoldPattern,
        Rtb,
        DefendUnit,
        AttackNearest,
        AllAttack,
        Escort,
    }

    /// <summary>Order and per-unit status (03 §4.2). Refusals happen at issue time and create no unit order.</summary>
    public enum OrderStatus
    {
        Executing = 0,
        Suspended,
        Achieved,
        Failed,
        Cancelled,
        Superseded,
    }

    /// <summary>Typed refusal at issue time (23 §6 point 10 rule 1). Wire form via <see cref="Orders.Wire(RefusalCode)"/>.</summary>
    public enum RefusalCode
    {
        None = 0,
        InvalidParams,
        EmptySelection,
        UnknownUnit,
        UnitDestroyed,
        NotOwnSide,
        NotPermitted,
        NotApplicable,
        NoBase,
        NotArmed,
        Winchester,
        NoWeaponManager,
        AiInactive,
        TargetUnknown,
        TargetDestroyed,
        TargetNotHostile,
        TargetNotInView,
        HookMissing,
    }

    /// <summary>Who issued an order; side-scope orders (RTB all, all attack) need the seat or the referee (09 §2.1.1).</summary>
    public enum IssuerRole
    {
        Seat = 0,
        Referee,
        Delegate,
        Script,
        Director,
    }

    public enum EscortSlot
    {
        Ahead = 0,
        Abreast,
        Astern,
    }

    /// <summary>Wire reason strings for terminal statuses (03 §4.2 reasons plus the C2 ones).</summary>
    public static class Reasons
    {
        public const string Arrived = "arrived";
        public const string OnStation = "on_station";
        public const string TargetsDestroyed = "targets_destroyed";
        public const string TargetLost = "target_lost";
        public const string OutOfAmmo = "out_of_ammo";
        public const string UnitDestroyed = "unit_destroyed";
        public const string AiInactive = "ai_inactive";
        public const string BaseLost = "base_lost";
        public const string ProtectedLost = "protected_lost";
        public const string EscorteeLost = "escortee_lost";
        public const string NothingLeft = "nothing_left";
        public const string Superseded = "superseded";
        public const string Cancelled = "cancelled";
        public const string Released = "released";
        public const string Winchester = "winchester";
    }

    /// <summary>
    /// An order as issued (c2.order.issue). Recipients: <see cref="Units"/>, a <see cref="Group"/>, or the whole
    /// <see cref="Side"/> with <see cref="SideScope"/>. Only the parameters of the chosen type are read.
    /// </summary>
    public sealed class OrderRequest
    {
        public OrderType Type;
        public string Issuer = "seat";
        public IssuerRole Role = IssuerRole.Seat;
        /// <summary>The issuing side. Units of other sides are refused with NotOwnSide.</summary>
        public string Side;
        public List<uint> Units = new List<uint>();
        public string Group;
        public bool SideScope;
        /// <summary>Side-scope filter (RTB all air): null = every domain.</summary>
        public Domain? DomainFilter;
        /// <summary>Free-text label for reports ("All aircraft (12)").</summary>
        public string Label;

        // move_to
        public GeoPoint Point;
        /// <summary>Altitude for aircraft in metres above terrain; NaN = keep the unit's altitude (or the pattern stack).</summary>
        public double AltitudeM = double.NaN;
        // move_route
        public List<GeoPoint> Route;
        // engage
        public uint Target;
        /// <summary>Explicit target set (the map's selection); otherwise the target's group or the hostiles clustered around it.</summary>
        public List<uint> Targets;
        // hold_pattern
        public GeoPoint PatternA, PatternB;
        public double WidthKm = double.NaN;
        public bool TurnRight = true;
        public double AltStackM = double.NaN;
        // rtb
        public uint Base;
        public WeaponsControl? Weapons;
        // defend_unit
        public uint Protect;
        public double RadiusKm = double.NaN;
        // escort
        public uint EscortOf;
        public EscortSlot Slot = EscortSlot.Ahead;
        public double DistanceKm = double.NaN;
        // attack_nearest / all_attack
        /// <summary>Raise ships' SAM engagement range to the weapon maximum (always on for all_attack).</summary>
        public bool ExtendRange;

        public static OrderRequest For(OrderType type, string side, params uint[] units)
        {
            var r = new OrderRequest { Type = type, Side = side };
            if (units != null) r.Units.AddRange(units);
            return r;
        }

        public static OrderRequest MoveTo(string side, GeoPoint p, params uint[] units) { var r = For(OrderType.MoveTo, side, units); r.Point = p; return r; }

        public static OrderRequest MoveRoute(string side, IList<GeoPoint> pts, params uint[] units) { var r = For(OrderType.MoveRoute, side, units); r.Route = new List<GeoPoint>(pts); return r; }

        public static OrderRequest Engage(string side, uint target, params uint[] units) { var r = For(OrderType.Engage, side, units); r.Target = target; return r; }

        public static OrderRequest HoldPattern(string side, GeoPoint a, GeoPoint b, params uint[] units) { var r = For(OrderType.HoldPattern, side, units); r.PatternA = a; r.PatternB = b; return r; }

        public static OrderRequest Rtb(string side, params uint[] units) { return For(OrderType.Rtb, side, units); }

        public static OrderRequest RtbAll(string side, Domain? filter = Domain.Air) { var r = For(OrderType.Rtb, side); r.SideScope = true; r.DomainFilter = filter; return r; }

        public static OrderRequest Defend(string side, uint vip, params uint[] units) { var r = For(OrderType.DefendUnit, side, units); r.Protect = vip; return r; }

        public static OrderRequest AttackNearest(string side, params uint[] units) { return For(OrderType.AttackNearest, side, units); }

        public static OrderRequest AllAttack(string side) { var r = For(OrderType.AllAttack, side); r.SideScope = true; r.ExtendRange = true; return r; }

        public static OrderRequest Escort(string side, uint leader, params uint[] units) { var r = For(OrderType.Escort, side, units); r.EscortOf = leader; return r; }
    }

    /// <summary>Per-unit outcome at issue time.</summary>
    public sealed class UnitOutcome
    {
        public uint Unit;
        public string Name;
        public bool Accepted;
        public RefusalCode Code;
        public string Reason;
        /// <summary>When the engine substituted another order (winchester hunter → RTB), its type.</summary>
        public OrderType? SubstitutedBy;
    }

    /// <summary>Result of <c>C2Engine.Issue</c>: the order (if any unit accepted) and the per-unit outcomes.</summary>
    public sealed class IssueResult
    {
        public Order Order;
        /// <summary>Whole-order refusal (no unit accepted). Per-unit codes are in <see cref="Units"/>.</summary>
        public RefusalCode Code;
        public string Message;
        public readonly List<UnitOutcome> Units = new List<UnitOutcome>();
        /// <summary>The operator-facing line, e.g. "Cobra 1, Cobra 2: RTB to Kestrel. 2 aircraft to the marshal stack 8 km astern."</summary>
        public string Summary;

        public bool Accepted => Order != null;

        public int AcceptedCount
        {
            get { int n = 0; foreach (var u in Units) if (u.Accepted) n++; return n; }
        }

        public UnitOutcome For(uint unit)
        {
            foreach (var u in Units) if (u.Unit == unit) return u;
            return null;
        }
    }

    /// <summary>An issued order; fans out to one <see cref="UnitOrder"/> per accepted unit (09 §2.2).</summary>
    public sealed class Order
    {
        public int Id;
        public OrderType Type;
        public string Issuer;
        public string Side;
        public OrderRequest Request;
        public OrderStatus Status;
        public string Reason;
        public double CreatedUt, UpdatedUt;
        public readonly List<UnitOrder> Units = new List<UnitOrder>();

        public string Handle => "o:" + Id.ToString(CultureInfo.InvariantCulture);
        public bool IsTerminal => Orders.IsTerminal(Status);
    }

    /// <summary>The part of an order one unit executes, with its per-type execution state.</summary>
    public sealed class UnitOrder
    {
        public Order Parent;
        public uint Unit;
        public OrderType Type;
        public OrderStatus Status;
        public string Reason;
        /// <summary>Execution phase for reports and tests: transit, on_station, marshal, engaging, hunting, holding, ...</summary>
        public string Phase = "";
        public double StartedUt;

        // movement
        internal GeoPoint Dest;
        internal List<GeoPoint> Path;
        internal double Alt = double.NaN;
        // engage / hunt / defend
        internal readonly List<uint> TargetSet = new List<uint>();
        internal uint Assigned;
        internal double NextPick;
        internal double IdleSince = double.NaN;
        // rtb
        internal uint Base;
        internal int MarshalSlot;
        internal bool ShipToHome;
        // defend / escort
        internal uint Anchor;
        internal double RingRadiusM;
        internal double StationBearing;
        internal double StationDistM;
        internal EscortSlot Slot;
        // route bookkeeping
        internal int RouteVersion;
        internal GeoPoint FedAnchor;
        internal bool Fed;
        internal int NextIndex;
        internal int FedStart;
        /// <summary>all_attack / extendRange: raise ships' SAM range to the weapon maximum.</summary>
        internal bool RangeMax;
        /// <summary>Weapons free for the duration of the order (all_attack).</summary>
        internal bool ForceFree;
        /// <summary>Pattern, escort: the unit reached its station once (for the progress report).</summary>
        internal bool Reached;

        public bool IsTerminal => Orders.IsTerminal(Status);
        public bool IsEngagement => Orders.IsEngagement(Type);
    }

    public static class Orders
    {
        public static bool IsTerminal(OrderStatus s) { return s == OrderStatus.Achieved || s == OrderStatus.Failed || s == OrderStatus.Cancelled || s == OrderStatus.Superseded; }

        /// <summary>Engagement orders suspend the unit's movement order and give it back when they end.</summary>
        public static bool IsEngagement(OrderType t) { return t == OrderType.Engage || t == OrderType.AttackNearest; }

        public static string Wire(OrderType t)
        {
            switch (t)
            {
                case OrderType.MoveTo: return "move_to";
                case OrderType.MoveRoute: return "move_route";
                case OrderType.Engage: return "engage";
                case OrderType.HoldPattern: return "hold_pattern";
                case OrderType.Rtb: return "rtb";
                case OrderType.DefendUnit: return "defend_unit";
                case OrderType.AttackNearest: return "attack_nearest";
                case OrderType.AllAttack: return "all_attack";
                case OrderType.Escort: return "escort";
                default: return t.ToString();
            }
        }

        public static bool TryParse(string s, out OrderType t)
        {
            for (int i = 0; i <= (int)OrderType.Escort; i++)
            {
                if (string.Equals(Wire((OrderType)i), s, StringComparison.OrdinalIgnoreCase)) { t = (OrderType)i; return true; }
            }
            t = OrderType.MoveTo;
            return false;
        }

        public static string Wire(OrderStatus s)
        {
            switch (s)
            {
                case OrderStatus.Executing: return "executing";
                case OrderStatus.Suspended: return "suspended";
                case OrderStatus.Achieved: return "achieved";
                case OrderStatus.Failed: return "failed";
                case OrderStatus.Cancelled: return "cancelled";
                default: return "superseded";
            }
        }

        public static string Wire(RefusalCode c)
        {
            switch (c)
            {
                case RefusalCode.InvalidParams: return "INVALID_PARAMS";
                case RefusalCode.EmptySelection: return "EMPTY_SELECTION";
                case RefusalCode.UnknownUnit: return "UNKNOWN_UNIT";
                case RefusalCode.UnitDestroyed: return "UNIT_DESTROYED";
                case RefusalCode.NotOwnSide: return "NOT_OWN_SIDE";
                case RefusalCode.NotPermitted: return "NOT_PERMITTED";
                case RefusalCode.NotApplicable: return "NOT_APPLICABLE";
                case RefusalCode.NoBase: return "NO_BASE";
                case RefusalCode.NotArmed: return "NOT_ARMED";
                case RefusalCode.Winchester: return "WINCHESTER";
                case RefusalCode.NoWeaponManager: return "NO_WEAPON_MANAGER";
                case RefusalCode.AiInactive: return "AI_INACTIVE";
                case RefusalCode.TargetUnknown: return "TARGET_UNKNOWN";
                case RefusalCode.TargetDestroyed: return "TARGET_DESTROYED";
                case RefusalCode.TargetNotHostile: return "TARGET_NOT_HOSTILE";
                case RefusalCode.TargetNotInView: return "TARGET_NOT_IN_VIEW";
                case RefusalCode.HookMissing: return "HOOK_MISSING";
                default: return "NONE";
            }
        }

        /// <summary>
        /// The protocol error kind a whole-order refusal maps to (03 §9), for the host's c2.order.issue handler.
        /// Per-unit refusals inside an accepted order are part of the result, not errors.
        /// </summary>
        public static string ErrorKind(RefusalCode c)
        {
            switch (c)
            {
                case RefusalCode.InvalidParams:
                case RefusalCode.EmptySelection:
                    return "INVALID_PARAMS";
                case RefusalCode.UnknownUnit:
                case RefusalCode.UnitDestroyed:
                case RefusalCode.TargetUnknown:
                case RefusalCode.TargetDestroyed:
                    return "NOT_FOUND";
                case RefusalCode.NotOwnSide:
                case RefusalCode.NotPermitted:
                    return "PERMISSION_DENIED";
                case RefusalCode.TargetNotInView:
                    return "NOT_IN_VIEW";
                case RefusalCode.HookMissing:
                    return "EXECUTOR_UNAVAILABLE";
                default:
                    return "PRECONDITION_FAILED";
            }
        }
    }
}
