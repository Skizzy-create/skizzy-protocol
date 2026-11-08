using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using Skizzy.Host.Core.World;
using UnityEngine;

namespace Skizzy.Host.Ksp
{
    /// <summary>
    /// Tier-0 reflection (04 §1): describes a PartModule's KSPFields, events and actions, and builds compiled field
    /// readers (02 §2.3: compiled accessors instead of <c>FieldInfo.GetValue</c>). Readers are IL getters emitted once
    /// per member; members of other types fall back to <c>BaseField.GetValue</c> (allocates, never in a skizzy:hot tag).
    /// </summary>
    internal static class ModuleReflector
    {
        internal static int Compiled, Fallbacks;

        internal static List<ModuleFieldInfo> Fields(PartModule m)
        {
            var list = new List<ModuleFieldInfo>();
            foreach (BaseField f in m.Fields)
            {
                Type t = MemberType(f);
                var info = new ModuleFieldInfo
                {
                    Name = f.name,
                    GuiName = string.IsNullOrEmpty(f.guiName) ? f.name : f.guiName,
                    Units = string.IsNullOrEmpty(f.guiUnits) ? null : f.guiUnits.Trim(),
                    ValueType = t != null ? t.Name : null,
                    JsonType = JsonType(t),
                    ShownInFlight = f.guiActive,
                    ShownInEditor = f.guiActiveEditor,
                };
                ApplyUi(info, f.uiControlFlight);
                list.Add(info);
            }
            return list;
        }

        internal static List<string> Events(PartModule m)
        {
            var l = new List<string>();
            foreach (BaseEvent e in m.Events) if (e != null) l.Add(e.name);
            return l;
        }

        internal static List<string> Actions(PartModule m)
        {
            var l = new List<string>();
            foreach (BaseAction a in m.Actions) if (a != null) l.Add(a.name);
            return l;
        }

        /// <summary>From attributes only, when no instance of the module exists (no part uses it).</summary>
        internal static ModuleTypeInfo DescribeType(Type t)
        {
            var fields = new List<ModuleFieldInfo>();
            var events = new List<string>();
            var actions = new List<string>();
            const BindingFlags Decl = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (var c = t; c != null && c != typeof(MonoBehaviour); c = c.BaseType)
            {
                foreach (var fi in c.GetFields(Decl))
                {
                    var a = Attribute.GetCustomAttribute(fi, typeof(KSPField), true) as KSPField;
                    if (a == null) continue;
                    var info = new ModuleFieldInfo
                    {
                        Name = fi.Name,
                        GuiName = string.IsNullOrEmpty(a.guiName) ? fi.Name : a.guiName,
                        Units = string.IsNullOrEmpty(a.guiUnits) ? null : a.guiUnits.Trim(),
                        ValueType = fi.FieldType.Name,
                        JsonType = JsonType(fi.FieldType),
                        ShownInFlight = a.guiActive,
                        ShownInEditor = a.guiActiveEditor,
                    };
                    var ui = Attribute.GetCustomAttribute(fi, typeof(UI_Control), true) as UI_Control;
                    ApplyUi(info, ui);
                    fields.Add(info);
                }
                foreach (var mi in c.GetMethods(Decl))
                {
                    if (Attribute.IsDefined(mi, typeof(KSPEvent), true)) events.Add(mi.Name);
                    if (Attribute.IsDefined(mi, typeof(KSPAction), true)) actions.Add(mi.Name);
                }
            }
            return new ModuleTypeInfo { ClassName = t.Name, Assembly = t.Assembly.GetName().Name, Fields = fields, Events = events, Actions = actions };
        }

        private static void ApplyUi(ModuleFieldInfo info, UI_Control ui)
        {
            if (ui == null || ui is UI_Label) return;
            info.Writable = ui.controlEnabled && (ui.scene & UI_Scene.Flight) != 0;
            if (ui is UI_FloatRange r) { info.Min = r.minValue; info.Max = r.maxValue; info.Step = r.stepIncrement; }
            else if (ui is UI_FloatEdit e) { info.Min = e.minValue; info.Max = e.maxValue; info.Step = e.incrementSlide; }
            else if (ui is UI_ChooseOption o) { info.Options = o.options; }
            else if (ui is UI_Toggle) { info.JsonType = "boolean"; }
        }

        internal static Type MemberType(BaseField f)
        {
            if (f.FieldInfo != null) return f.FieldInfo.FieldType;
            var pi = f.MemberInfo as PropertyInfo;
            return pi != null ? pi.PropertyType : null;
        }

        internal static string JsonType(Type t)
        {
            if (t == null) return "string";
            if (t == typeof(float) || t == typeof(double) || t == typeof(decimal)) return "number";
            if (t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) || t == typeof(uint) ||
                t == typeof(ushort) || t == typeof(ulong) || t == typeof(sbyte)) return "integer";
            if (t == typeof(bool)) return "boolean";
            return "string";
        }

        internal static IFieldAccessor CreateAccessor(KspModule owner, BaseField f)
        {
            object host = f.host ?? owner.M;
            MemberInfo mi = f.FieldInfo != null ? f.FieldInfo : f.MemberInfo;
            Type t = MemberType(f);
            bool compilable = mi != null && t != null && host != null && mi.DeclaringType != null && mi.DeclaringType.IsInstanceOfType(host)
                              && !(mi is FieldInfo fi && fi.IsStatic) && !(mi is PropertyInfo pi && pi.GetGetMethod(true) == null);
            if (compilable)
            {
                IFieldAccessor a = null;
                if (t == typeof(float)) { var g = FieldGetters.Get<float>(mi); if (g != null) a = new SingleReader(owner, host, g); }
                else if (t == typeof(double)) { var g = FieldGetters.Get<double>(mi); if (g != null) a = new DoubleReader(owner, host, g); }
                else if (t == typeof(int)) { var g = FieldGetters.Get<int>(mi); if (g != null) a = new IntReader(owner, host, g); }
                else if (t == typeof(long)) { var g = FieldGetters.Get<long>(mi); if (g != null) a = new LongReader(owner, host, g); }
                else if (t == typeof(bool)) { var g = FieldGetters.Get<bool>(mi); if (g != null) a = new BoolReader(owner, host, g); }
                else if (t == typeof(string)) { var g = FieldGetters.Get<string>(mi); if (g != null) a = new StringReader(owner, host, g); }
                else if (t.IsEnum && Enum.GetUnderlyingType(t) == typeof(int)) { var g = FieldGetters.Get<int>(mi); if (g != null) a = new EnumReader(owner, host, g, t); }
                if (a != null) { Compiled++; return a; }
            }
            Fallbacks++;
            return new BoxedReader(owner, host, f);
        }

        private abstract class Reader : IFieldAccessor
        {
            protected readonly KspModule Owner;
            protected readonly object Host;
            protected Reader(KspModule owner, object host) { Owner = owner; Host = host; }
            public abstract bool Read(ref FieldValue v);
        }

        private sealed class SingleReader : Reader
        {
            private readonly Func<object, float> _g;
            public SingleReader(KspModule o, object h, Func<object, float> g) : base(o, h) { _g = g; }
            // skizzy:hot ModuleReflector.SingleReader.Read
            public override bool Read(ref FieldValue v) { if (Owner.M == null) return false; v.SetSingle(_g(Host)); return true; }
        }

        private sealed class DoubleReader : Reader
        {
            private readonly Func<object, double> _g;
            public DoubleReader(KspModule o, object h, Func<object, double> g) : base(o, h) { _g = g; }
            // skizzy:hot ModuleReflector.DoubleReader.Read
            public override bool Read(ref FieldValue v) { if (Owner.M == null) return false; v.SetNum(_g(Host)); return true; }
        }

        private sealed class IntReader : Reader
        {
            private readonly Func<object, int> _g;
            public IntReader(KspModule o, object h, Func<object, int> g) : base(o, h) { _g = g; }
            // skizzy:hot ModuleReflector.IntReader.Read
            public override bool Read(ref FieldValue v) { if (Owner.M == null) return false; v.SetInt(_g(Host)); return true; }
        }

        private sealed class LongReader : Reader
        {
            private readonly Func<object, long> _g;
            public LongReader(KspModule o, object h, Func<object, long> g) : base(o, h) { _g = g; }
            // skizzy:hot ModuleReflector.LongReader.Read
            public override bool Read(ref FieldValue v) { if (Owner.M == null) return false; v.SetInt(_g(Host)); return true; }
        }

        private sealed class BoolReader : Reader
        {
            private readonly Func<object, bool> _g;
            public BoolReader(KspModule o, object h, Func<object, bool> g) : base(o, h) { _g = g; }
            // skizzy:hot ModuleReflector.BoolReader.Read
            public override bool Read(ref FieldValue v) { if (Owner.M == null) return false; v.SetBool(_g(Host)); return true; }
        }

        private sealed class StringReader : Reader
        {
            private readonly Func<object, string> _g;
            public StringReader(KspModule o, object h, Func<object, string> g) : base(o, h) { _g = g; }
            // skizzy:hot ModuleReflector.StringReader.Read
            public override bool Read(ref FieldValue v) { if (Owner.M == null) return false; v.SetStr(_g(Host)); return true; }
        }

        private sealed class EnumReader : Reader
        {
            private readonly Func<object, int> _g;
            private readonly Type _type;
            private readonly Dictionary<int, string> _names = new Dictionary<int, string>();

            public EnumReader(KspModule o, object h, Func<object, int> g, Type type) : base(o, h)
            {
                _g = g;
                _type = type;
                foreach (var e in Enum.GetValues(type)) _names[Convert.ToInt32(e, CultureInfo.InvariantCulture)] = e.ToString();
            }

            // skizzy:hot ModuleReflector.EnumReader.Read
            public override bool Read(ref FieldValue v)
            {
                if (Owner.M == null) return false;
                int x = _g(Host);
                if (!_names.TryGetValue(x, out var s)) _names[x] = s = Enum.ToObject(_type, x).ToString(); // flag combinations: once each
                v.SetStr(s);
                return true;
            }
        }

        /// <summary>Fallback for types without a compiled reader (vectors, structs, custom types): allocates per read.</summary>
        private sealed class BoxedReader : Reader
        {
            private readonly BaseField _f;
            public BoxedReader(KspModule o, object h, BaseField f) : base(o, h) { _f = f; }

            public override bool Read(ref FieldValue v)
            {
                if (Owner.M == null) return false;
                object o = _f.GetValue(Host);
                if (o == null) { v.SetNull(); return true; }
                if (o is float fl) v.SetSingle(fl);
                else if (o is double d) v.SetNum(d);
                else if (o is int i) v.SetInt(i);
                else if (o is long l) v.SetInt(l);
                else if (o is uint u) v.SetInt(u);
                else if (o is short sh) v.SetInt(sh);
                else if (o is byte b) v.SetInt(b);
                else if (o is bool bo) v.SetBool(bo);
                else if (o is string s) v.SetStr(s);
                else v.SetStr(Convert.ToString(o, CultureInfo.InvariantCulture));
                return true;
            }
        }
    }

    /// <summary>IL getters <c>object -&gt; T</c> for instance fields and properties, cached per member.</summary>
    internal static class FieldGetters
    {
        private static readonly Dictionary<MemberInfo, Delegate> Cache = new Dictionary<MemberInfo, Delegate>();
        internal static string LastError;

        internal static Func<object, T> Get<T>(MemberInfo m)
        {
            lock (Cache)
            {
                if (Cache.TryGetValue(m, out var d)) return d as Func<object, T>;
            }
            Func<object, T> f = null;
            try
            {
                var dm = new DynamicMethod("skizzy_get_" + m.Name, typeof(T), new[] { typeof(object) }, typeof(FieldGetters).Module, true);
                var il = dm.GetILGenerator();
                Type decl = m.DeclaringType;
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(decl.IsValueType ? OpCodes.Unbox : OpCodes.Castclass, decl);
                if (m is FieldInfo fi) il.Emit(OpCodes.Ldfld, fi);
                else
                {
                    var getter = ((PropertyInfo)m).GetGetMethod(true);
                    il.Emit(getter.IsVirtual && !decl.IsValueType ? OpCodes.Callvirt : OpCodes.Call, getter);
                }
                il.Emit(OpCodes.Ret);
                f = (Func<object, T>)dm.CreateDelegate(typeof(Func<object, T>));
            }
            catch (Exception e)
            {
                LastError = m.DeclaringType + "." + m.Name + ": " + e.GetType().Name + ": " + e.Message;
                f = null;
            }
            lock (Cache) Cache[m] = f;
            return f;
        }
    }
}
