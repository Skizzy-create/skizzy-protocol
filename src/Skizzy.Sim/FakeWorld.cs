using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Skizzy.Host.Core;
using Skizzy.Host.Core.World;
using Skizzy.Protocol.Catalog;
using Skizzy.Protocol.Json;

namespace Skizzy.Sim
{
    /// <summary>
    /// FakeKsp (02 §1, D13): an <see cref="IGameWorld"/> with ships, planes and debris whose state follows simple
    /// kinematics in game time. Game time runs from a stopwatch (scaled, pausable) so it advances whether or not the
    /// host pump is running, as in KSP.
    /// </summary>
    public sealed class FakeWorld : IGameWorld
    {
        private readonly object _gate = new object();
        private readonly List<FakeVessel> _vessels = new List<FakeVessel>();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double _utBase = 1200.0;
        private double _clockBase;
        private double _timeScale = 1;
        private bool _paused;
        private int _topology = 1;
        private int _epoch = 1;
        private uint _nextPid = 3281937421;
        public string Scene = "FLIGHT";
        public FakeVessel Active;
        public readonly FakeCamera FakeCamera;
        public HostCore Host;          // optional: lets world changes raise host events like the KSP GameEvents do
        public string LogFile;
        public readonly List<ModInfo> Mods = new List<ModInfo>();
        public long ReadMetaCalls;

        public FakeWorld()
        {
            FakeCamera = new FakeCamera(this);
            Mods.Add(new ModInfo { Name = "Skizzy.Host", Version = "0.1.0", Folder = "SkizzyProtocol", File = "GameData/SkizzyProtocol/Plugins/Skizzy.Host.dll", Kind = "assembly" });
            Mods.Add(new ModInfo { Name = "BDArmory", Version = "1.8.0", Folder = "BDArmory", File = "GameData/BDArmory/Plugins/BDArmory.dll", Kind = "assembly" });
            Mods.Add(new ModInfo { Name = "SkizzyProtocol", Folder = "SkizzyProtocol", Kind = "folder" });
        }

        // ---------------------------------------------------------------- time

        public double Ut
        {
            get
            {
                lock (_gate) return _paused ? _utBase : _utBase + (_clock.Elapsed.TotalSeconds - _clockBase) * _timeScale;
            }
        }

        public void SetPaused(bool paused)
        {
            lock (_gate)
            {
                if (paused == _paused) return;
                _utBase = UtUnlocked();
                _clockBase = _clock.Elapsed.TotalSeconds;
                _paused = paused;
            }
            if (Host != null) Host.Dispatcher.EnqueueMain(new Job { Action = () => { if (paused) Host.WorldEvents.Paused(); else Host.WorldEvents.Resumed(); } });
        }

        public void SetTimeScale(double scale)
        {
            lock (_gate)
            {
                _utBase = UtUnlocked();
                _clockBase = _clock.Elapsed.TotalSeconds;
                _timeScale = scale;
            }
        }

        private double UtUnlocked() { return _paused ? _utBase : _utBase + (_clock.Elapsed.TotalSeconds - _clockBase) * _timeScale; }

        // ---------------------------------------------------------------- fixtures

        /// <summary>The default naval test fixture (scenario 16 §0 flavour).</summary>
        public static FakeWorld CreateDefault()
        {
            var w = new FakeWorld();
            var ddg1 = w.AddVessel("DDG-1 Kirov", "Ship", "SPLASHED", -0.0972, -74.5577, 1.2, 15.4, 90, 0.7, true);
            ddg1.AddPart("bdaShipHull", "Destroyer Hull", "ModuleHullBreach", "ModuleEngines");
            ddg1.AddPart("bdaWeaponManager", "Weapon Manager", "MissileFire");
            ddg1.AddPart("bdaRadar", "AN/SPY-1 Radar", "ModuleRadar");
            ddg1.AddPart("bdaVLS", "Mk41 VLS", "MissileLauncher", "ModuleResourceIntake");
            ddg1.AddPart("bdaSurfaceAI", "Surface AI", "BDModuleSurfaceAI");
            w.Active = ddg1;
            var ddg2 = w.AddVessel("DDG-2 Udaloy", "Ship", "SPLASHED", -0.0901, -74.5401, 1.0, 14.0, 95, 0.6, false);
            ddg2.AddPart("bdaShipHull", "Destroyer Hull", "ModuleHullBreach", "ModuleEngines");
            ddg2.AddPart("bdaWeaponManager", "Weapon Manager", "MissileFire");
            var ffg = w.AddVessel("FFG-3 Neustrashimy", "Ship", "SPLASHED", -0.1100, -74.5700, 0.8, 12.0, 270, 0.5, false);
            ffg.AddPart("bdaShipHull", "Frigate Hull", "ModuleHullBreach", "ModuleEngines");
            var f14 = w.AddVessel("F-14 Tomcat", "Plane", "FLYING", -0.0500, -74.4000, 3200, 240, 180, 0.85, false);
            f14.AddPart("mk1pod", "Mk1 Cockpit", "ModuleCommand");
            f14.AddPart("turboFanEngine", "J-404 Panther", "ModuleEngines");
            f14.AddPart("bdaPilotAI", "Pilot AI", "BDModulePilotAI");
            w.AddVessel("Debris (Mk41 cap)", "Debris", "SPLASHED", -0.0975, -74.5580, 0, 0, 0, 0, false).AddPart("cap", "Cap");
            var far = w.AddVessel("Tanker (far)", "Ship", "SPLASHED", 5.2, -60.1, 0.5, 8, 10, 0.3, false);
            far.LoadedFlag = false;
            far.PartCountUnloaded = 40;
            return w;
        }

        public FakeVessel AddVessel(string name, string type, string situation, double lat, double lon, double alt, double speed, double heading, double throttle, bool active)
        {
            var v = new FakeVessel(this, _nextPid++, name, type)
            {
                Situation = situation,
                Lat0 = lat,
                Lon0 = lon,
                Alt = alt,
                Speed = speed,
                Heading = heading,
                Throttle = (float)throttle,
                Ut0 = Ut,
            };
            lock (_gate) _vessels.Add(v);
            if (active) Active = v;
            BumpTopology();
            return v;
        }

        /// <summary>Adds <paramref name="n"/> loaded ships with three parts each (log/stream perf tests).</summary>
        public void AddFleet(int n, string prefix = "SIM")
        {
            for (int i = 0; i < n; i++)
            {
                var v = AddVessel(prefix + "-" + (i + 1).ToString("000", CultureInfo.InvariantCulture), "Ship", "SPLASHED",
                    -0.2 + i * 0.001, -74.6 + i * 0.001, 1, 10 + i % 7, (i * 37) % 360, 0.5, false);
                v.AddPart("hull", "Hull", "ModuleHullBreach", "ModuleEngines");
                v.AddPart("wm", "Weapon Manager", "MissileFire");
            }
        }

        public void BumpTopology() { lock (_gate) _topology++; }

        public void DestroyVessel(FakeVessel v)
        {
            lock (_gate) _vessels.Remove(v);
            v.Alive = false;
            if (Active == v) Active = null;
            BumpTopology();
            Host?.Dispatcher.EnqueueMain(new Job { Action = () => Host.WorldEvents.VesselDestroyed(v.Pid, v.VesselName, v.Handle) });
        }

        /// <summary>Simulates a scene switch (20 §5): epoch increments; vessels stay (KSP keeps persistentIds).</summary>
        public void ChangeScene(string to, bool dropVessels = false)
        {
            string from = Scene;
            if (Host != null) Host.Dispatcher.EnqueueMain(new Job { Action = () => Host.WorldEvents.SceneChangeRequested(from, to) });
            lock (_gate)
            {
                Scene = to;
                _epoch++;
                if (dropVessels) { foreach (var v in _vessels) v.Alive = false; _vessels.Clear(); Active = null; }
            }
            BumpTopology();
            if (Host != null) Host.Dispatcher.EnqueueMain(new Job { Action = () => Host.WorldEvents.SceneLoaded(to) });
        }

        public FakeVessel ByName(string name)
        {
            lock (_gate) foreach (var v in _vessels) if (v.VesselName == name) return v;
            return null;
        }

        // ---------------------------------------------------------------- IGameWorld

        public void ReadMeta(ref MetaState m)
        {
            ReadMetaCalls++;
            m.Ut = Ut;
            m.Frame = (long)(m.Ut * 50);
            m.Scene = Scene;
            lock (_gate) { m.Paused = _paused; m.TimeScale = _paused ? 0 : _timeScale; }
            m.WarpMode = "none";
            m.WarpRate = 1;
            var a = Active;
            m.ActiveVessel = a != null && Scene == "FLIGHT" ? a.Handle : null;
            m.ActiveVesselName = a != null && Scene == "FLIGHT" ? a.VesselName : null;
            m.SceneTransition = false;
        }

        public int TopologyVersion { get { lock (_gate) return _topology; } }
        public int SceneEpoch { get { lock (_gate) return _epoch; } }

        public void GetVessels(List<GVessel> into)
        {
            lock (_gate) foreach (var v in _vessels) into.Add(v);
        }

        public GVessel FindVessel(uint pid)
        {
            lock (_gate) foreach (var v in _vessels) if (v.Pid == pid) return v;
            return null;
        }

        public GPart FindPart(uint pid)
        {
            if (Scene != "FLIGHT") return null;
            lock (_gate)
                foreach (var v in _vessels)
                    if (v.Loaded)
                        foreach (var p in v.Parts) if (p.Pid == pid) return p;
            return null;
        }

        public GVessel ActiveVessel => Scene == "FLIGHT" ? Active : null;

        public ModuleTypeInfo DescribeModuleType(string className)
        {
            lock (_gate)
                foreach (var v in _vessels)
                    foreach (var p in v.Parts)
                        foreach (var m in p.Modules)
                            if (m.ClassName == className)
                                return new ModuleTypeInfo { ClassName = className, Assembly = "FakeKsp", Fields = m.Fields, Events = m.Events, Actions = m.Actions };
            return null;
        }

        public void GetMods(List<ModInfo> into) { into.AddRange(Mods); }

        public ICameraService Camera => FakeCamera;

        public string LogPath => LogFile;

        public void Doctor(List<string[]> lines)
        {
            lines.Add(new[] { "pass", "game", "FakeKsp (no game running)" });
            lines.Add(new[] { "warn", "CommNet", "not simulated" });
        }

        internal uint NextPid() { return _nextPid++; }
    }

    public sealed class FakeVessel : GVessel
    {
        private readonly FakeWorld _w;
        public readonly uint Pid;
        public string VesselName;
        public readonly string Type;
        private readonly string _handle;
        public string Situation;
        public double Lat0, Lon0, Alt, Speed, Heading, Ut0;
        public float Throttle;
        public bool Alive = true;
        public bool LoadedFlag = true;
        public bool IsPacked;
        public int PartCountUnloaded;
        public readonly List<FakePart> Parts = new List<FakePart>();

        public FakeVessel(FakeWorld w, uint pid, string name, string type)
        {
            _w = w;
            Pid = pid;
            VesselName = name;
            Type = type;
            _handle = "v:" + pid.ToString(CultureInfo.InvariantCulture);
        }

        public FakePart AddPart(string name, string title, params string[] modules)
        {
            var p = new FakePart(this, _w.NextPid(), name, title);
            for (int i = 0; i < modules.Length; i++) p.Modules.Add(new FakeModule(p, i, modules[i]));
            Parts.Add(p);
            _w.BumpTopology();
            return p;
        }

        public override uint PersistentId => Pid;
        public override string Name => VesselName;
        public override string Handle => _handle;
        public override string TypeName => Type;
        public override bool IsAlive => Alive;
        public override bool Loaded => LoadedFlag && _w.Scene == "FLIGHT";
        public override bool Packed => IsPacked;
        public override bool IsActive => _w.Active == this && _w.Scene == "FLIGHT";
        public override int PartCount => LoadedFlag ? Parts.Count : PartCountUnloaded;
        public override int LoadedPartCount => Loaded ? Parts.Count : 0;
        public override GPart GetPart(int index) { return Parts[index]; }

        private double Elapsed => _w.Ut - Ut0;
        private const double MPerDeg = 600000.0 * Math.PI / 180.0; // Kerbin radius 600 km

        public double Lat => Lat0 + Math.Cos(Heading * Math.PI / 180) * Speed * Elapsed / MPerDeg;
        public double Lon => Lon0 + Math.Sin(Heading * Math.PI / 180) * Speed * Elapsed / MPerDeg;

        // skizzy:hot FakeVessel.ReadField
        public override bool ReadField(int index, ref FieldValue v)
        {
            bool live = Loaded;
            switch (VesselFields.All[index].Name)
            {
                case "name": v.SetStr(VesselName); return true;
                case "handle": v.SetStr(_handle); return true;
                case "type": v.SetStr(Type); return true;
                case "situation": v.SetStr(Situation); return true;
                case "body": v.SetStr("Kerbin"); return true;
                case "lat": v.SetNum(Lat); return true;
                case "lon": v.SetNum(Lon); return true;
                case "altitude": v.SetNum(Alt); return true;
                case "heightFromTerrain": if (live) v.SetNum(Type == "Plane" ? Alt - 50 : 120); else v.SetNull(); return true;
                case "srfSpeed": v.SetNum(Speed); return true;
                case "obtSpeed": v.SetNum(Speed + 174.5); return true;
                case "verticalSpeed": v.SetNum(0); return true;
                case "horizontalSrfSpeed": v.SetNum(Speed); return true;
                case "heading": case "course": if (live) v.SetNum(Heading); else v.SetNull(); return true;
                case "pitch": case "roll": if (live) v.SetNum(0); else v.SetNull(); return true;
                case "mach": if (live) v.SetNum(Speed / 340.0); else v.SetNull(); return true;
                case "geeForce": if (live) v.SetNum(1); else v.SetNull(); return true;
                case "dynamicPressurekPa": if (live) v.SetNum(0.5 * 1.225 * Speed * Speed / 1000); else v.SetNull(); return true;
                case "staticPressurekPa": if (live) v.SetNum(101.325); else v.SetNull(); return true;
                case "atmDensity": if (live) v.SetNum(1.225); else v.SetNull(); return true;
                case "externalTemperature": if (live) v.SetNum(288); else v.SetNull(); return true;
                case "mass": if (live) v.SetNum(Parts.Count * 12.5); else v.SetNull(); return true;
                case "missionTime": v.SetNum(Elapsed); return true;
                case "partCount": v.SetInt(PartCount); return true;
                case "crewCount": v.SetInt(Type == "Debris" ? 0 : 2); return true;
                case "currentStage": if (live) v.SetInt(0); else v.SetNull(); return true;
                case "loaded": v.SetBool(live); return true;
                case "packed": v.SetBool(IsPacked); return true;
                case "landed": v.SetBool(Situation == "LANDED"); return true;
                case "splashed": v.SetBool(Situation == "SPLASHED"); return true;
                case "isActive": v.SetBool(IsActive); return true;
                case "controllable": if (live) v.SetBool(Type != "Debris"); else v.SetNull(); return true;
                case "ctrlState.mainThrottle": if (live) v.SetSingle(Throttle); else v.SetNull(); return true;
                case "ctrlState.pitch": case "ctrlState.yaw": case "ctrlState.roll":
                case "ctrlState.wheelThrottle": case "ctrlState.wheelSteer":
                    if (live) v.SetSingle(0f); else v.SetNull(); return true;
                case "actionGroups.sas": if (live) v.SetBool(true); else v.SetNull(); return true;
                case "actionGroups.rcs": case "actionGroups.gear": case "actionGroups.brakes": case "actionGroups.lights":
                    if (live) v.SetBool(false); else v.SetNull(); return true;
            }
            v.SetNull();
            return false;
        }
    }

    public sealed class FakePart : GPart
    {
        private readonly FakeVessel _v;
        public readonly uint Pid;
        private readonly string _name, _title, _handle;
        public readonly List<FakeModule> Modules = new List<FakeModule>();
        public double LiquidFuel = 1800, LiquidFuelMax = 3600;

        public FakePart(FakeVessel v, uint pid, string name, string title)
        {
            _v = v;
            Pid = pid;
            _name = name;
            _title = title;
            _handle = "p:" + pid.ToString(CultureInfo.InvariantCulture);
        }

        public override uint PersistentId => Pid;
        public override string Handle => _handle;
        public override string Name => _name;
        public override string Title => _title;
        public override GVessel Vessel => _v;
        public override int ModuleCount => Modules.Count;
        public override GModule GetModule(int index) { return Modules[index]; }

        public override bool ReadField(int index, ref FieldValue v)
        {
            switch (PartFields.All[index].Name)
            {
                case "name": v.SetStr(_name); return true;
                case "title": v.SetStr(_title); return true;
                case "handle": v.SetStr(_handle); return true;
                case "mass": v.SetNum(12.5); return true;
                case "temperature": v.SetNum(300); return true;
                case "maxTemp": v.SetNum(2000); return true;
                case "skinTemperature": v.SetNum(295); return true;
                case "crewCount": v.SetInt(0); return true;
                case "inverseStage": v.SetInt(0); return true;
            }
            v.SetNull();
            return false;
        }

        public override bool ReadResource(string resource, bool max, ref FieldValue v)
        {
            if (resource != "LiquidFuel") return false;
            v.SetNum(max ? LiquidFuelMax : LiquidFuel);
            return true;
        }
    }

    public sealed class FakeModule : GModule
    {
        private readonly FakePart _p;
        private readonly int _index;
        private readonly string _class, _handle;
        private readonly List<ModuleFieldInfo> _fields = new List<ModuleFieldInfo>();
        public readonly Dictionary<string, double> Values = new Dictionary<string, double>();
        public readonly Dictionary<string, string> Strings = new Dictionary<string, string>();
        private static readonly string[] NoNames = new string[0];
        private readonly string[] _events, _actions;

        public FakeModule(FakePart p, int index, string className)
        {
            _p = p;
            _index = index;
            _class = className;
            _handle = "m:" + p.PersistentId.ToString(CultureInfo.InvariantCulture) + ":" + index.ToString(CultureInfo.InvariantCulture) + ":" + className;
            switch (className)
            {
                case "ModuleHullBreach":
                    Num("FloodRate", "Flood Rate", "t/s", 0); Num("hydroExplosiveMultiplier", "Hydro Explosive Mult", null, 1); Bool("isHullBreached", "Breached", false);
                    _events = new[] { "ToggleHullBreach" }; _actions = NoNames; break;
                case "ModuleEngines":
                    Num("thrustPercentage", "Thrust Limiter", "%", 100, 0, 100); Num("finalThrust", "Thrust", "kN", 210); Num("fuelFlowGui", "Fuel Flow", "U", 1.2);
                    Bool("EngineIgnited", "Ignited", true);
                    _events = new[] { "Activate", "Shutdown" }; _actions = new[] { "OnAction", "ShutdownAction", "ActivateAction" }; break;
                case "MissileFire":
                    Str("teamString", "Team", "Blue"); Num("targetScanInterval", "Target Scan Interval", "s", 3, 0.5, 60); Bool("guardMode", "Guard Mode", true);
                    Num("maxMissilesOnTarget", "Max Missiles on Target", null, 2, 1, 6); Num("gunRange", "Gun Range", "m", 2500, 0, 10000);
                    _events = new[] { "ToggleGuardMode" }; _actions = new[] { "AGToggleGuardMode" }; break;
                case "ModuleRadar":
                    Bool("radarEnabled", "Radar Enabled", true); Num("radarMaxDistanceDetect", "Max Range", "km", 120);
                    _events = new[] { "Toggle" }; _actions = NoNames; break;
                case "BDModuleSurfaceAI": case "BDModulePilotAI":
                    Bool("pilotEnabled", "Pilot Enabled", true); Num("steerMult", "Steer Factor", null, 6, 0.1, 20);
                    _events = new[] { "TogglePilot" }; _actions = NoNames; break;
                default:
                    _events = NoNames; _actions = NoNames; break;
            }
        }

        private void Num(string name, string gui, string units, double value, double? min = null, double? max = null)
        {
            _fields.Add(new ModuleFieldInfo { Name = name, GuiName = gui, Units = units, JsonType = "number", ValueType = "Single", ShownInFlight = true, Writable = min.HasValue, Min = min, Max = max });
            Values[name] = value;
        }

        private void Bool(string name, string gui, bool value)
        {
            _fields.Add(new ModuleFieldInfo { Name = name, GuiName = gui, JsonType = "boolean", ValueType = "Boolean", ShownInFlight = true, Writable = true });
            Values[name] = value ? 1 : 0;
        }

        private void Str(string name, string gui, string value)
        {
            _fields.Add(new ModuleFieldInfo { Name = name, GuiName = gui, JsonType = "string", ValueType = "String", ShownInFlight = true });
            Strings[name] = value;
        }

        public override string ClassName => _class;
        public override int Index => _index;
        public override GPart Part => _p;
        public override string Handle => _handle;
        public override IList<ModuleFieldInfo> Fields => _fields;
        public override IList<string> Events => _events;
        public override IList<string> Actions => _actions;

        public override IFieldAccessor CreateAccessor(string field)
        {
            foreach (var f in _fields)
                if (f.Name == field) return new Accessor(this, f.Name, f.JsonType);
            return null;
        }

        private sealed class Accessor : IFieldAccessor
        {
            private readonly FakeModule _m;
            private readonly string _f;
            private readonly string _type;
            public Accessor(FakeModule m, string f, string type) { _m = m; _f = f; _type = type; }

            // skizzy:hot FakeModule.Accessor.Read
            public bool Read(ref FieldValue v)
            {
                if (!((FakeVessel)_m._p.Vessel).Alive) return false;
                if (_type == "string") { v.SetStr(_m.Strings[_f]); return true; }
                double d = _m.Values[_f];
                if (_type == "boolean") v.SetBool(d != 0); else v.SetSingle((float)d);
                return true;
            }
        }
    }
}
