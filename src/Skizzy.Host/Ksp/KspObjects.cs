using System;
using System.Collections.Generic;
using System.Globalization;
using Skizzy.Host.Core.World;
using Skizzy.Protocol.Catalog;
using UnityEngine;

namespace Skizzy.Host.Ksp
{
    /// <summary>Vessel wrapper. Field reads go through a precomputed index-to-code table (no string switch, no allocation).</summary>
    internal sealed class KspVessel : GVessel
    {
        internal readonly Vessel V;
        private readonly KspWorld _w;
        private readonly uint _pid;
        private readonly string _handle;
        private string _lastName;
        private bool _dead;

        internal KspVessel(KspWorld w, Vessel v)
        {
            _w = w;
            V = v;
            _pid = v.persistentId;
            _handle = "v:" + _pid.ToString(CultureInfo.InvariantCulture);
            _lastName = v.vesselName;
        }

        internal void MarkDead()
        {
            if (V != null) _lastName = V.vesselName;
            _dead = true;
        }

        public override uint PersistentId => _pid;
        public override string Name => IsAlive ? V.vesselName : _lastName;
        public override string Handle => _handle;
        public override string TypeName => IsAlive ? KspNames.VesselType(V.vesselType) : "Unknown";
        // skizzy:hot KspVessel.IsAlive
        public override bool IsAlive => !_dead && V != null && V.persistentId == _pid && V.state != Vessel.State.DEAD;
        public override bool Loaded => IsAlive && V.loaded;
        public override bool Packed => IsAlive && V.packed;
        public override bool IsActive => IsAlive && V.isActiveVessel;

        public override int PartCount
        {
            get
            {
                if (!IsAlive) return 0;
                if (V.loaded) return V.parts.Count;
                return V.protoVessel != null ? V.protoVessel.protoPartSnapshots.Count : 0;
            }
        }

        public override int LoadedPartCount => Loaded ? V.parts.Count : 0;

        public override GPart GetPart(int index) { return _w.WrapPart(V.parts[index]); }

        // skizzy:hot KspVessel.ReadField
        public override bool ReadField(int index, ref FieldValue v)
        {
            var V = this.V;
            bool live = V.loaded;
            switch (VesselCodes.Of(index))
            {
                case VF.Name: v.SetStr(V.vesselName); return true;
                case VF.Handle: v.SetStr(_handle); return true;
                case VF.Type: v.SetStr(KspNames.VesselType(V.vesselType)); return true;
                case VF.Situation: v.SetStr(KspNames.Situation(V.situation)); return true;
                case VF.Body: v.SetStr(V.mainBody != null ? V.mainBody.bodyName : null); return true;
                case VF.Lat: v.SetNum(V.latitude); return true;
                case VF.Lon: v.SetNum(V.longitude); return true;
                case VF.Altitude: v.SetNum(V.altitude); return true;
                case VF.HeightFromTerrain: if (live) v.SetNum(V.heightFromTerrain); else v.SetNull(); return true;
                case VF.SrfSpeed: v.SetNum(V.srfSpeed); return true;
                case VF.ObtSpeed: v.SetNum(V.obt_speed); return true;
                case VF.VerticalSpeed: v.SetNum(V.verticalSpeed); return true;
                case VF.HorizontalSrfSpeed: v.SetNum(V.horizontalSrfSpeed); return true;
                case VF.Heading: case VF.Pitch: case VF.Roll:
                    if (live && Attitude.Read(V, VesselCodes.Of(index), out double a)) v.SetNum(a); else v.SetNull();
                    return true;
                case VF.Course:
                    if (live && Attitude.Course(V, out double c)) v.SetNum(c); else v.SetNull();
                    return true;
                case VF.Mach: if (live) v.SetNum(V.mach); else v.SetNull(); return true;
                case VF.GeeForce: if (live) v.SetNum(V.geeForce); else v.SetNull(); return true;
                case VF.DynamicPressure: if (live) v.SetNum(V.dynamicPressurekPa); else v.SetNull(); return true;
                case VF.StaticPressure: if (live) v.SetNum(V.staticPressurekPa); else v.SetNull(); return true;
                case VF.AtmDensity: if (live) v.SetNum(V.atmDensity); else v.SetNull(); return true;
                case VF.ExternalTemperature: if (live) v.SetNum(V.externalTemperature); else v.SetNull(); return true;
                case VF.Mass: if (live) v.SetNum(V.totalMass); else v.SetNull(); return true;
                case VF.MissionTime: v.SetNum(V.missionTime); return true;
                case VF.PartCount: v.SetInt(PartCount); return true;
                case VF.CrewCount: v.SetInt(V.GetCrewCount()); return true;
                case VF.CurrentStage: if (live) v.SetInt(V.currentStage); else v.SetNull(); return true;
                case VF.Loaded: v.SetBool(live); return true;
                case VF.Packed: v.SetBool(V.packed); return true;
                case VF.Landed: v.SetBool(V.Landed); return true;
                case VF.Splashed: v.SetBool(V.Splashed); return true;
                case VF.IsActive: v.SetBool(V.isActiveVessel); return true;
                case VF.Controllable: if (live) v.SetBool(V.IsControllable); else v.SetNull(); return true;
                case VF.Throttle: if (live) v.SetSingle(V.ctrlState.mainThrottle); else v.SetNull(); return true;
                case VF.CtrlPitch: if (live) v.SetSingle(V.ctrlState.pitch); else v.SetNull(); return true;
                case VF.CtrlYaw: if (live) v.SetSingle(V.ctrlState.yaw); else v.SetNull(); return true;
                case VF.CtrlRoll: if (live) v.SetSingle(V.ctrlState.roll); else v.SetNull(); return true;
                case VF.WheelThrottle: if (live) v.SetSingle(V.ctrlState.wheelThrottle); else v.SetNull(); return true;
                case VF.WheelSteer: if (live) v.SetSingle(V.ctrlState.wheelSteer); else v.SetNull(); return true;
                case VF.AgSas: if (live) v.SetBool(V.ActionGroups[KSPActionGroup.SAS]); else v.SetNull(); return true;
                case VF.AgRcs: if (live) v.SetBool(V.ActionGroups[KSPActionGroup.RCS]); else v.SetNull(); return true;
                case VF.AgGear: if (live) v.SetBool(V.ActionGroups[KSPActionGroup.Gear]); else v.SetNull(); return true;
                case VF.AgBrakes: if (live) v.SetBool(V.ActionGroups[KSPActionGroup.Brakes]); else v.SetNull(); return true;
                case VF.AgLights: if (live) v.SetBool(V.ActionGroups[KSPActionGroup.Light]); else v.SetNull(); return true;
            }
            v.SetNull();
            return false;
        }
    }

    internal enum VF
    {
        Unknown, Name, Handle, Type, Situation, Body, Lat, Lon, Altitude, HeightFromTerrain, SrfSpeed, ObtSpeed, VerticalSpeed,
        HorizontalSrfSpeed, Heading, Pitch, Roll, Course, Mach, GeeForce, DynamicPressure, StaticPressure, AtmDensity,
        ExternalTemperature, Mass, MissionTime, PartCount, CrewCount, CurrentStage, Loaded, Packed, Landed, Splashed, IsActive,
        Controllable, Throttle, CtrlPitch, CtrlYaw, CtrlRoll, WheelThrottle, WheelSteer, AgSas, AgRcs, AgGear, AgBrakes, AgLights,
    }

    /// <summary>Maps <see cref="VesselFields.All"/> indices to <see cref="VF"/> codes once.</summary>
    internal static class VesselCodes
    {
        private static readonly VF[] Codes = Build();

        internal static VF Of(int index) { return (uint)index < (uint)Codes.Length ? Codes[index] : VF.Unknown; }

        /// <summary>Field names the KSP layer does not implement (self-check: must be empty).</summary>
        internal static List<string> Missing()
        {
            var l = new List<string>();
            for (int i = 0; i < Codes.Length; i++) if (Codes[i] == VF.Unknown) l.Add(VesselFields.All[i].Name);
            return l;
        }

        private static VF[] Build()
        {
            var map = new Dictionary<string, VF>
            {
                { "name", VF.Name }, { "handle", VF.Handle }, { "type", VF.Type }, { "situation", VF.Situation }, { "body", VF.Body },
                { "lat", VF.Lat }, { "lon", VF.Lon }, { "altitude", VF.Altitude }, { "heightFromTerrain", VF.HeightFromTerrain },
                { "srfSpeed", VF.SrfSpeed }, { "obtSpeed", VF.ObtSpeed }, { "verticalSpeed", VF.VerticalSpeed },
                { "horizontalSrfSpeed", VF.HorizontalSrfSpeed }, { "heading", VF.Heading }, { "pitch", VF.Pitch }, { "roll", VF.Roll },
                { "course", VF.Course }, { "mach", VF.Mach }, { "geeForce", VF.GeeForce }, { "dynamicPressurekPa", VF.DynamicPressure },
                { "staticPressurekPa", VF.StaticPressure }, { "atmDensity", VF.AtmDensity }, { "externalTemperature", VF.ExternalTemperature },
                { "mass", VF.Mass }, { "missionTime", VF.MissionTime }, { "partCount", VF.PartCount }, { "crewCount", VF.CrewCount },
                { "currentStage", VF.CurrentStage }, { "loaded", VF.Loaded }, { "packed", VF.Packed }, { "landed", VF.Landed },
                { "splashed", VF.Splashed }, { "isActive", VF.IsActive }, { "controllable", VF.Controllable },
                { "ctrlState.mainThrottle", VF.Throttle }, { "ctrlState.pitch", VF.CtrlPitch }, { "ctrlState.yaw", VF.CtrlYaw },
                { "ctrlState.roll", VF.CtrlRoll }, { "ctrlState.wheelThrottle", VF.WheelThrottle }, { "ctrlState.wheelSteer", VF.WheelSteer },
                { "actionGroups.sas", VF.AgSas }, { "actionGroups.rcs", VF.AgRcs }, { "actionGroups.gear", VF.AgGear },
                { "actionGroups.brakes", VF.AgBrakes }, { "actionGroups.lights", VF.AgLights },
            };
            var codes = new VF[VesselFields.All.Length];
            for (int i = 0; i < codes.Length; i++) codes[i] = map.TryGetValue(VesselFields.All[i].Name, out var c) ? c : VF.Unknown;
            return codes;
        }
    }

    /// <summary>
    /// Surface-relative attitude from KSP's per-frame local frame (<c>Vessel.upAxis/north/east</c>). The nose is the
    /// reference transform's up vector and the top of the craft is minus its forward vector (KSP part convention).
    /// </summary>
    internal static class Attitude
    {
        private const double Rad2Deg = 180.0 / Math.PI;

        // skizzy:hot Attitude.Read
        internal static bool Read(Vessel v, VF which, out double value)
        {
            value = 0;
            Transform t = v.ReferenceTransform;
            if (t == null) return false;
            Vector3d up = v.upAxis, north = v.north, east = v.east;
            Vector3d nose = t.up;
            double u = Vector3d.Dot(nose, up);
            if (which == VF.Heading)
            {
                double h = Math.Atan2(Vector3d.Dot(nose, east), Vector3d.Dot(nose, north)) * Rad2Deg;
                value = h < 0 ? h + 360 : h;
                return true;
            }
            if (which == VF.Pitch)
            {
                value = Math.Asin(u < -1 ? -1 : u > 1 ? 1 : u) * Rad2Deg;
                return true;
            }
            // Roll: angle of the craft's top around the nose, from the local vertical; positive = right wing down.
            Vector3d levelTop = up - nose * u;
            if (levelTop.sqrMagnitude < 1e-8) { value = 0; return true; }
            levelTop = levelTop.normalized;
            Vector3d levelRight = Vector3d.Cross(levelTop, nose).normalized;
            Vector3d top = -(Vector3d)t.forward;
            value = Math.Atan2(Vector3d.Dot(top, levelRight), Vector3d.Dot(top, levelTop)) * Rad2Deg;
            return true;
        }

        internal static bool Course(Vessel v, out double value)
        {
            Vector3d vel = v.srf_velocity;
            double n = Vector3d.Dot(vel, v.north), e = Vector3d.Dot(vel, v.east);
            if (n * n + e * e < 0.01) { value = 0; return false; }
            double c = Math.Atan2(e, n) * Rad2Deg;
            value = c < 0 ? c + 360 : c;
            return true;
        }
    }

    /// <summary>Enum names cached once, so reads never call Enum.ToString (which allocates).</summary>
    internal static class KspNames
    {
        private static readonly Dictionary<int, string> Situations = Names(typeof(Vessel.Situations));
        private static readonly Dictionary<int, string> VesselTypes = Names(typeof(global::VesselType));

        private static Dictionary<int, string> Names(Type enumType)
        {
            var d = new Dictionary<int, string>();
            foreach (var v in Enum.GetValues(enumType)) d[Convert.ToInt32(v, CultureInfo.InvariantCulture)] = v.ToString();
            return d;
        }

        internal static string Situation(Vessel.Situations s) { return Situations.TryGetValue((int)s, out var n) ? n : "UNKNOWN"; }
        internal static string VesselType(global::VesselType t) { return VesselTypes.TryGetValue((int)t, out var n) ? n : "Unknown"; }
    }

    internal sealed class KspPart : GPart
    {
        internal readonly Part P;
        private readonly KspWorld _w;
        private readonly uint _pid;
        private readonly string _handle, _name, _title;
        private KspModule[] _modules = new KspModule[0];

        internal KspPart(KspWorld w, Part p)
        {
            _w = w;
            P = p;
            _pid = p.persistentId;
            _handle = "p:" + _pid.ToString(CultureInfo.InvariantCulture);
            _name = p.partInfo != null ? p.partInfo.name : p.name;
            _title = p.partInfo != null ? p.partInfo.title : _name;
        }

        public override uint PersistentId => _pid;
        public override string Handle => _handle;
        public override string Name => _name;
        public override string Title => _title;
        public override GVessel Vessel => P != null ? _w.Wrap(P.vessel) : null;
        public override int ModuleCount => P != null ? P.Modules.Count : 0;

        public override GModule GetModule(int index)
        {
            var mods = P.Modules;
            if (_modules.Length != mods.Count) _modules = new KspModule[mods.Count];
            var w = _modules[index];
            var m = mods[index];
            if (w == null || !ReferenceEquals(w.M, m)) _modules[index] = w = new KspModule(this, m, index);
            return w;
        }

        // skizzy:hot KspPart.ReadField
        public override bool ReadField(int index, ref FieldValue v)
        {
            if (P == null) { v.SetNull(); return false; }
            switch (index)
            {
                case 0: v.SetStr(_name); return true;
                case 1: v.SetStr(_title); return true;
                case 2: v.SetStr(_handle); return true;
                case 3: v.SetNum(P.mass + P.GetResourceMass()); return true;
                case 4: v.SetNum(P.temperature); return true;
                case 5: v.SetNum(P.maxTemp); return true;
                case 6: v.SetNum(P.skinTemperature); return true;
                case 7: v.SetInt(P.protoModuleCrew != null ? P.protoModuleCrew.Count : 0); return true;
                case 8: v.SetInt(P.inverseStage); return true;
            }
            v.SetNull();
            return false;
        }

        /// <summary>Checks at startup that <see cref="ReadField"/>'s index order still matches <see cref="PartFields.All"/>.</summary>
        internal static bool TableMatches()
        {
            var expect = new[] { "name", "title", "handle", "mass", "temperature", "maxTemp", "skinTemperature", "crewCount", "inverseStage" };
            if (PartFields.All.Length != expect.Length) return false;
            for (int i = 0; i < expect.Length; i++) if (PartFields.All[i].Name != expect[i]) return false;
            return true;
        }

        // skizzy:hot KspPart.ReadResource
        public override bool ReadResource(string resource, bool max, ref FieldValue v)
        {
            if (P == null) return false;
            var r = P.Resources.Get(resource);
            if (r == null) return false;
            v.SetNum(max ? r.maxAmount : r.amount);
            return true;
        }
    }

    internal sealed class KspModule : GModule
    {
        internal readonly PartModule M;
        private readonly KspPart _p;
        private readonly int _index;
        private readonly string _class, _handle;
        private List<ModuleFieldInfo> _fields;
        private List<string> _events, _actions;

        internal KspModule(KspPart p, PartModule m, int index)
        {
            _p = p;
            M = m;
            _index = index;
            _class = m != null ? m.GetType().Name : "Missing";
            _handle = "m:" + p.PersistentId.ToString(CultureInfo.InvariantCulture) + ":" + index.ToString(CultureInfo.InvariantCulture) + ":" + _class;
        }

        public override string ClassName => _class;
        public override int Index => _index;
        public override GPart Part => _p;
        public override string Handle => _handle;
        public override IList<ModuleFieldInfo> Fields => _fields ?? (_fields = M != null ? ModuleReflector.Fields(M) : new List<ModuleFieldInfo>());
        public override IList<string> Events => _events ?? (_events = M != null ? ModuleReflector.Events(M) : new List<string>());
        public override IList<string> Actions => _actions ?? (_actions = M != null ? ModuleReflector.Actions(M) : new List<string>());

        public override IFieldAccessor CreateAccessor(string field)
        {
            if (M == null || string.IsNullOrEmpty(field)) return null;
            BaseField f = M.Fields[field];
            return f == null ? null : ModuleReflector.CreateAccessor(this, f);
        }
    }
}
