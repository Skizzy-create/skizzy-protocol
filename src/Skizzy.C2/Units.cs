using System;

namespace Skizzy.C2
{
    public enum Domain
    {
        Air,
        Surface,
        Subsurface,
        Missile,
        Land,
    }

    /// <summary>
    /// What a unit is, as far as orders and doctrine care. The game binding classifies vessels (pilot AI and
    /// weapon manager → fighter; large radar and no weapons → AEW; surface AI → ship; flight deck → carrier ...).
    /// </summary>
    public enum UnitClass
    {
        Unknown = 0,
        Fighter,
        Drone,
        Bomber,
        Aew,
        Carrier,
        Ship,
        Submarine,
        Airfield,
        Missile,
        Ground,
    }

    public static class UnitClasses
    {
        public static Domain DomainOf(UnitClass c)
        {
            switch (c)
            {
                case UnitClass.Fighter:
                case UnitClass.Drone:
                case UnitClass.Bomber:
                case UnitClass.Aew:
                    return Domain.Air;
                case UnitClass.Carrier:
                case UnitClass.Ship:
                    return Domain.Surface;
                case UnitClass.Submarine:
                    return Domain.Subsurface;
                case UnitClass.Missile:
                    return Domain.Missile;
                default:
                    return Domain.Land;
            }
        }

        public static bool IsAircraft(UnitClass c) { return DomainOf(c) == Domain.Air; }

        /// <summary>Air combat units: the ones that hunt, advance on contact, divert and self-defend.</summary>
        public static bool IsAirCombat(UnitClass c) { return c == UnitClass.Fighter || c == UnitClass.Drone; }

        public static bool IsShip(UnitClass c) { return c == UnitClass.Carrier || c == UnitClass.Ship; }

        /// <summary>Hostile aircraft that hunters and VIP protection treat as aircraft targets.</summary>
        public static bool IsAircraftTarget(UnitClass c) { return c == UnitClass.Fighter || c == UnitClass.Bomber || c == UnitClass.Drone || c == UnitClass.Aew; }

        /// <summary>Wide-area sensor platforms: a track they hold is part of the shared picture (row 7).</summary>
        public static bool IsSurveillance(UnitClass c) { return c == UnitClass.Aew || c == UnitClass.Carrier || c == UnitClass.Ship || c == UnitClass.Airfield; }

        public static string Name(UnitClass c)
        {
            switch (c)
            {
                case UnitClass.Fighter: return "fighter";
                case UnitClass.Drone: return "drone";
                case UnitClass.Bomber: return "bomber";
                case UnitClass.Aew: return "aew";
                case UnitClass.Carrier: return "carrier";
                case UnitClass.Ship: return "ship";
                case UnitClass.Submarine: return "submarine";
                case UnitClass.Airfield: return "airfield";
                case UnitClass.Missile: return "missile";
                case UnitClass.Ground: return "ground";
                default: return "unknown";
            }
        }
    }

    /// <summary>
    /// One unit as the game binding reports it each tick. The binding owns these objects and may reuse them;
    /// the engine copies what it keeps (<see cref="CopyFrom"/>). Missiles in flight are units too (class Missile).
    /// </summary>
    public sealed class UnitState
    {
        /// <summary>Vessel persistentId. Never 0.</summary>
        public uint Id;
        public string Name;
        /// <summary>BDArmory team name.</summary>
        public string Side;
        public UnitClass Class;
        /// <summary>Scenario or team grouping known to the binding (may be null). Used to spread an attack across a target group.</summary>
        public string Group;
        public bool Alive;
        public GeoPoint Pos;
        public double HeadingDeg;
        public double SpeedMps;
        /// <summary>The unit's BDA AI is active (<c>pilotEnabled</c>); F6 and BDA commands silently do nothing otherwise.</summary>
        public bool AiActive;
        /// <summary>Landed or on deck (aircraft); false for airborne aircraft and for ships.</summary>
        public bool Landed;
        public bool HasWeaponManager;
        public bool GuardMode;
        /// <summary>Offensive missiles ready (AAM for aircraft, SAM/ASM for ships), counted as BDA does (rail ammo plus magazines).</summary>
        public int MissilesReady;
        /// <summary>Offensive missile capacity; 0 = the unit carries no offensive missiles.</summary>
        public int MissilesMax;
        /// <summary>Longest offensive missile engagement range in metres (0 = none).</summary>
        public double MissileRangeM;
        public bool HasGuns;
        /// <summary>Fuel fraction 0..1, or NaN when unknown.</summary>
        public double Fuel = double.NaN;
        /// <summary>Carrier or airfield this unit launched from (0 = none).</summary>
        public uint HomeBase;
        /// <summary>Home station (spawn point for ships), valid when <see cref="HasHome"/>.</summary>
        public GeoPoint Home;
        public bool HasHome;
        /// <summary>Carrier or airfield: can recover aircraft and is an RTB destination.</summary>
        public bool IsBase;
        /// <summary>For missiles: the vessel it is guiding on (0 = unknown).</summary>
        public uint MissileTarget;
        /// <summary>For missiles: the vessel that launched it (0 = unknown). Lets the picture declare the launcher hostile (08 §12.1).</summary>
        public uint Launcher;
        /// <summary>For missiles: the weapon (BDArmory short name, else the part title); null = unknown.</summary>
        public string Weapon;
        /// <summary>
        /// Funds value from KSP's own part costs (catalog cost plus module costs, summed over every part of the vessel, so a
        /// multi-part or modular missile counts all its parts); NaN = unknown. The binding fills it for missiles.
        /// </summary>
        public double Cost = double.NaN;
        /// <summary>Vertical speed, m/s, up positive (0 when the binding does not report it).</summary>
        public double ClimbMps;

        public Domain Domain => UnitClasses.DomainOf(Class);
        public bool IsMissile => Class == UnitClass.Missile;

        public void CopyFrom(UnitState s)
        {
            Id = s.Id;
            Name = s.Name;
            Side = s.Side;
            Class = s.Class;
            Group = s.Group;
            Alive = s.Alive;
            Pos = s.Pos;
            HeadingDeg = s.HeadingDeg;
            SpeedMps = s.SpeedMps;
            AiActive = s.AiActive;
            Landed = s.Landed;
            HasWeaponManager = s.HasWeaponManager;
            GuardMode = s.GuardMode;
            MissilesReady = s.MissilesReady;
            MissilesMax = s.MissilesMax;
            MissileRangeM = s.MissileRangeM;
            HasGuns = s.HasGuns;
            Fuel = s.Fuel;
            HomeBase = s.HomeBase;
            Home = s.Home;
            HasHome = s.HasHome;
            IsBase = s.IsBase;
            MissileTarget = s.MissileTarget;
            Launcher = s.Launcher;
            Weapon = s.Weapon;
            Cost = s.Cost;
            ClimbMps = s.ClimbMps;
        }

        public bool IsArmed => MissilesMax > 0 || HasGuns;
    }

    /// <summary>
    /// One line of a unit list (the in-game C2 window, a future <c>c2.unit.list</c>): a value copy of what the engine
    /// knows about a unit, filled without per-unit allocation (<see cref="C2Engine.ListUnits"/>).
    /// </summary>
    public struct UnitBrief
    {
        public uint Id;
        public string Name;
        public string Side;
        public UnitClass Class;
        /// <summary>The binding's grouping (callsign group from the vessel name); may be null.</summary>
        public string Group;
        /// <summary>The C2 group set with <see cref="C2Engine.SetGroup"/>; may be null.</summary>
        public string C2Group;
        public bool Alive;
        /// <summary>Loaded and reported by the binding this tick (false: out of physics range, or gone).</summary>
        public bool Present;
        public bool AiActive;
        public bool HasWeaponManager;
        public bool Landed;
        public bool IsBase;
        public int MissilesReady;
        public int MissilesMax;
        /// <summary>What drives the unit now: an order type, "hold", "advance", "vip_priority" or "guard".</summary>
        public string Task;
        /// <summary>The order driving the unit (engagement first), 0 = none.</summary>
        public int Order;
        /// <summary>Effective doctrine weapons control.</summary>
        public WeaponsControl WeaponsControl;
        /// <summary>F3 fire authority the engine last applied.</summary>
        public FireAuthorityMode FireAuthority;
    }
}
