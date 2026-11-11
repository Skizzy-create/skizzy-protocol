using System;
using System.Collections.Generic;
using Skizzy.Protocol.Catalog;
using Skizzy.Protocol.Errors;
using Skizzy.Protocol.Json;

namespace Skizzy.Host.Core.World
{
    /// <summary>Game state copied once per active frame and attached to every result as <c>meta</c> (03 §2).</summary>
    public struct MetaState
    {
        public double Ut;
        public long Frame;
        public string Scene;
        public bool Paused;
        public double TimeScale;
        public string WarpMode;   // none | rails | physics
        public double WarpRate;
        public string ActiveVessel;      // handle or null
        public string ActiveVesselName;
        public bool SceneTransition;
    }

    /// <summary>A field value without boxing. Strings must come from caches (never built per read).</summary>
    public struct FieldValue
    {
        public FieldKind Kind;
        public double Num;
        public bool Bool;
        public string Str;

        public void SetNull() { Kind = FieldKind.Null; Str = null; }
        public void SetNum(double v) { Kind = FieldKind.Number; Num = v; Str = null; }
        public void SetSingle(float v) { Kind = FieldKind.Single; Num = v; Str = null; }
        public void SetInt(long v) { Kind = FieldKind.Integer; Num = v; Str = null; }
        public void SetBool(bool v) { Kind = FieldKind.Bool; Bool = v; Str = null; }
        public void SetStr(string v) { Kind = v == null ? FieldKind.Null : FieldKind.String; Str = v; }

        public bool IsNumeric => Kind == FieldKind.Number || Kind == FieldKind.Single || Kind == FieldKind.Integer;
        public double AsNumber => Kind == FieldKind.Bool ? (Bool ? 1 : 0) : Num;

        public JsonValue ToJson()
        {
            switch (Kind)
            {
                case FieldKind.Bool: return JsonValue.Bool(Bool);
                case FieldKind.Number: return double.IsNaN(Num) || double.IsInfinity(Num) ? JsonValue.Null : JsonValue.Num(Num);
                case FieldKind.Single: return float.IsNaN((float)Num) || float.IsInfinity((float)Num) ? JsonValue.Null : JsonValue.Num(Math.Round(Num, 6));
                case FieldKind.Integer: return JsonValue.Num((long)Num);
                case FieldKind.String: return JsonValue.Str(Str);
                default: return JsonValue.Null;
            }
        }

        public bool SameAs(ref FieldValue o, double deadband)
        {
            if (Kind != o.Kind) return false;
            switch (Kind)
            {
                case FieldKind.Null: return true;
                case FieldKind.Bool: return Bool == o.Bool;
                case FieldKind.String: return string.Equals(Str, o.Str, StringComparison.Ordinal);
                default:
                    if (double.IsNaN(Num) || double.IsNaN(o.Num)) return double.IsNaN(Num) && double.IsNaN(o.Num);
                    return Math.Abs(Num - o.Num) <= deadband;
            }
        }
    }

    /// <summary>A compiled read of one value (vessel field, part field or module KSPField). Main thread only.</summary>
    public interface IFieldAccessor
    {
        /// <summary>Reads the value; returns false if the target no longer exists.</summary>
        bool Read(ref FieldValue v);
    }

    /// <summary>A vessel as the host sees it. KSP wraps <c>Vessel</c>; FakeKsp implements it directly.</summary>
    public abstract class GVessel
    {
        public abstract uint PersistentId { get; }
        public abstract string Name { get; }
        /// <summary>"v:&lt;pid&gt;", cached.</summary>
        public abstract string Handle { get; }
        public abstract string TypeName { get; }
        public abstract bool IsAlive { get; }
        public abstract bool Loaded { get; }
        public abstract bool Packed { get; }
        public abstract bool IsActive { get; }
        public bool IsDebris => TypeName == "Debris" || TypeName == "SpaceObject" || TypeName == "Unknown";
        public abstract int PartCount { get; }
        /// <summary>Loaded parts (0 when unloaded).</summary>
        public abstract int LoadedPartCount { get; }
        public abstract GPart GetPart(int index);
        /// <summary>Reads <c>VesselFields.All[index]</c>. Must not allocate.</summary>
        public abstract bool ReadField(int index, ref FieldValue v);
        /// <summary>BDArmory presence for ksp.vessel.get (M0: by module class name only).</summary>
        public virtual bool HasModule(string className)
        {
            int n = LoadedPartCount;
            for (int i = 0; i < n; i++)
            {
                var p = GetPart(i);
                for (int m = 0; m < p.ModuleCount; m++) if (p.GetModule(m).ClassName == className) return true;
            }
            return false;
        }
    }

    public abstract class GPart
    {
        public abstract uint PersistentId { get; }
        public abstract string Handle { get; }
        public abstract string Name { get; }
        public abstract string Title { get; }
        public abstract GVessel Vessel { get; }
        public abstract int ModuleCount { get; }
        public abstract GModule GetModule(int index);
        /// <summary>Reads <c>PartFields.All[index]</c> (no allocation).</summary>
        public abstract bool ReadField(int index, ref FieldValue v);
        /// <summary>Resource amount/max by resource name; false if the part has no such resource.</summary>
        public abstract bool ReadResource(string resource, bool max, ref FieldValue v);
    }

    public sealed class ModuleFieldInfo
    {
        public string Name;
        public string GuiName;
        public string Units;
        public string JsonType;    // number | integer | boolean | string
        public string ValueType;   // CLR type name
        public bool ShownInFlight;
        public bool ShownInEditor;
        public bool Writable;      // UI control allows changes (M1 writes)
        public double? Min, Max, Step;
        public string[] Options;
    }

    public abstract class GModule
    {
        public abstract string ClassName { get; }
        public abstract int Index { get; }
        public abstract GPart Part { get; }
        /// <summary>"m:&lt;partPid&gt;:&lt;index&gt;:&lt;Class&gt;", cached.</summary>
        public abstract string Handle { get; }
        public abstract IList<ModuleFieldInfo> Fields { get; }
        public abstract IList<string> Events { get; }
        public abstract IList<string> Actions { get; }
        /// <summary>Compiles an accessor for a KSPField; null if the module has no such field.</summary>
        public abstract IFieldAccessor CreateAccessor(string field);
    }

    public sealed class ModInfo
    {
        public string Name;
        public string Version;
        public string Folder;
        public string File;
        public string Kind;   // assembly | folder
    }

    public sealed class ModuleTypeInfo
    {
        public string ClassName;
        public string Assembly;
        public IList<ModuleFieldInfo> Fields;
        public IList<string> Events;
        public IList<string> Actions;
    }

    public sealed class CaptureRequest
    {
        public int Width = 1024;
        public bool Png;
        public int Quality = 75;
        public bool HideUi;
    }

    public sealed class CaptureResult
    {
        public byte[] Data;      // encoded image (JPEG or PNG); may be pooled by the service
        public int Length;
        public int Width, Height;
        public string MimeType;
        public double MainThreadMs;
        public double TotalMs;
        public string Path;      // "async_readback" | "readpixels"
        public SkizzyError Error;
    }

    public struct OrbitSpec
    {
        public bool HasHeading, HasPitch, HasDistance;
        public double Heading, Pitch, Distance;
    }

    /// <summary>Camera control and capture (13 §1). Main thread; capture completes in a later frame.</summary>
    public interface ICameraService
    {
        void Capture(CaptureRequest req, Action<CaptureResult> done);
        /// <summary>mode: follow|orbit|chase|free|look_at|auto|orbital|locked. Returns null or an error.</summary>
        SkizzyError SetMode(string mode, GVessel target, GVessel to, OrbitSpec orbit);
        /// <summary>
        /// Called after a successful SetMode: reports (on the main thread, within a few frames) null when the game kept
        /// the mode, or an error when it switched to another one (KSP refuses the orbital camera near the ground).
        /// </summary>
        void ConfirmMode(Action<SkizzyError> done);
        SkizzyError SetTarget(GVessel target);
        SkizzyError SetOrbit(OrbitSpec orbit);
        SkizzyError LookAt(GVessel from, GVessel to, double distance, double elevationDeg);
        JsonValue State();
        bool Available { get; }
    }

    /// <summary>
    /// The game as the host engines see it (02 §1.1). <c>Ksp/</c> implements it for KSP, <c>Skizzy.Sim</c> for CI.
    /// Every member is main-thread only unless stated.
    /// </summary>
    public interface IGameWorld
    {
        void ReadMeta(ref MetaState m);
        /// <summary>Changes whenever vessels, parts or modules are added, removed, loaded or unloaded.</summary>
        int TopologyVersion { get; }
        /// <summary>Increments on every scene load (handles from older epochs are stale).</summary>
        int SceneEpoch { get; }
        void GetVessels(List<GVessel> into);
        GVessel FindVessel(uint persistentId);
        GPart FindPart(uint persistentId);
        GVessel ActiveVessel { get; }
        ModuleTypeInfo DescribeModuleType(string className);
        void GetMods(List<ModInfo> into);
        ICameraService Camera { get; }
        /// <summary>Any thread. Path of KSP.log for ksp.log.tail.</summary>
        string LogPath { get; }
        /// <summary>Main thread. Diagnostic lines for host.doctor and host.selfcheck (pass/warn/fail).</summary>
        void Doctor(List<string[]> lines);
        /// <summary>
        /// Main thread. Pauses the game for a pause-on-event (C2 FirstLaunch, 24 row 27). Returns false when this scene
        /// cannot pause or the game is already paused.
        /// </summary>
        bool RequestPause(string reason);
    }

    /// <summary>Wakes the main-thread pump (UnitySynchronizationContext post, or the Sim runner).</summary>
    public interface IMainThreadWaker
    {
        /// <summary>Any thread. Schedules one main-thread callback that re-enables the pump.</summary>
        void Wake();
        /// <summary>"sync_context" or "polling" (02 §2; spike 3).</summary>
        string Mode { get; }
    }
}
