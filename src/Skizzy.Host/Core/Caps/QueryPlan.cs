using System;
using System.Collections.Generic;
using System.Text;
using Skizzy.Host.Core.World;
using Skizzy.Protocol.Catalog;
using Skizzy.Protocol.Errors;
using Skizzy.Protocol.Json;
using Skizzy.Protocol.Util;

namespace Skizzy.Host.Core.Caps
{
    public enum Aggregate { PerVessel, PerPart, PerModule }
    public enum Reduce { First, Sum, Min, Max, Avg }

    /// <summary>A parsed selector + field list (04 §2), shared by ksp.query, streams and log v0.</summary>
    public sealed class QuerySpec
    {
        public bool? Active, Loaded;
        public List<string> Handles;
        public string NameLike;
        public HashSet<string> Types;
        public bool IncludeDebris;
        public bool HasVesselFilter;
        public bool HasParts;
        public string PartNameLike, PartTitleLike;
        public bool HasModules;
        public HashSet<string> ModuleTypes;
        public string[] Fields;
        public Aggregate Aggregate;
        public Reduce Reduce;
        public FieldRef[] Refs;

        public static QuerySpec FromParams(JsonValue p)
        {
            var q = new QuerySpec();
            var sel = p.GetObj("select") ?? JsonValue.Obj();
            var v = sel.GetObj("vessels");
            if (v != null)
            {
                q.HasVesselFilter = true;
                if (v.Has("active")) q.Active = v.GetBool("active");
                if (v.Has("loaded")) q.Loaded = v.GetBool("loaded");
                q.Handles = v.GetStringList("handles");
                q.NameLike = v.GetString("nameLike");
                q.Types = StrOrList(v.Get("type"));
                q.IncludeDebris = v.GetBool("includeDebris", false);
            }
            var parts = sel.GetObj("parts");
            if (parts != null)
            {
                q.HasParts = true;
                q.PartNameLike = parts.GetString("nameLike");
                q.PartTitleLike = parts.GetString("titleLike");
            }
            var mods = sel.GetObj("modules");
            if (mods != null)
            {
                q.HasModules = true;
                q.ModuleTypes = StrOrList(mods.Get("type"));
            }
            var f = p.GetStringList("fields");
            q.Fields = f != null ? f.ToArray() : new string[0];
            switch (p.GetString("aggregate", "perVessel"))
            {
                case "perPart": q.Aggregate = Aggregate.PerPart; break;
                case "perModule": q.Aggregate = Aggregate.PerModule; break;
                default: q.Aggregate = Aggregate.PerVessel; break;
            }
            switch (p.GetString("reduce", "first"))
            {
                case "sum": q.Reduce = Reduce.Sum; break;
                case "min": q.Reduce = Reduce.Min; break;
                case "max": q.Reduce = Reduce.Max; break;
                case "avg": q.Reduce = Reduce.Avg; break;
                default: q.Reduce = Reduce.First; break;
            }
            if (q.Aggregate == Aggregate.PerModule && !q.HasModules)
                throw SkizzyError.InvalidParam("/aggregate", "perModule needs select.modules", "Add select.modules.type, e.g. [\"ModuleEngines\"].");
            if (q.Aggregate == Aggregate.PerPart && !q.HasParts && !q.HasModules) q.HasParts = true;
            q.Refs = new FieldRef[q.Fields.Length];
            for (int i = 0; i < q.Fields.Length; i++) q.Refs[i] = FieldRef.Parse(q.Fields[i], q, "/fields/" + i);
            return q;
        }

        private static HashSet<string> StrOrList(JsonValue v)
        {
            if (v == null || v.IsNull) return null;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (v.IsString) set.Add(v.AsString());
            else if (v.IsArray) foreach (var x in v.Items) if (x.IsString) set.Add(x.AsString());
            return set.Count == 0 ? null : set;
        }

        public bool VesselMatches(GVessel v)
        {
            if (!v.IsAlive) return false;
            if (Handles != null && Handles.Count > 0) return true; // explicit list (resolved separately)
            if (Active.HasValue && v.IsActive != Active.Value) return false;
            if (Loaded.HasValue) { if (v.Loaded != Loaded.Value) return false; }
            else if (!HasVesselFilter && !v.Loaded) return false; // no selector: every loaded vessel
            if (!IncludeDebris && v.IsDebris && !(Types != null && Types.Contains(v.TypeName))) return false;
            if (NameLike != null && !Glob.IsMatch(v.Name, NameLike)) return false;
            if (Types != null && !Types.Contains(v.TypeName)) return false;
            return true;
        }

        public bool PartMatches(GPart p)
        {
            if (PartNameLike != null && !Glob.IsMatch(p.Name, PartNameLike)) return false;
            if (PartTitleLike != null && !Glob.IsMatch(p.Title, PartTitleLike)) return false;
            return true;
        }

        public bool ModuleMatches(GModule m)
        {
            return ModuleTypes == null || ModuleTypes.Contains(m.ClassName);
        }

        public bool NeedsParts => HasParts || HasModules || Aggregate != Aggregate.PerVessel;
    }

    public enum FieldSource { Vessel, Part, Resource, Module }

    public sealed class FieldRef
    {
        public string Name;
        public FieldSource Source;
        public int Index;
        public string Resource;
        public bool Max;
        public string ModuleField;
        public FieldKind Kind;

        public static FieldRef Parse(string name, QuerySpec q, string pointer)
        {
            var r = new FieldRef { Name = name };
            if (name.StartsWith("vessel.", StringComparison.Ordinal))
            {
                int vi = VesselFields.IndexOf(name);
                if (vi < 0) throw Unknown(name, pointer, "vessel");
                return r.AsVessel(vi);
            }
            if (name.StartsWith("part.", StringComparison.Ordinal))
            {
                if (r.TryPart(name)) return r;
                throw Unknown(name, pointer, "part");
            }
            switch (q.Aggregate)
            {
                case Aggregate.PerVessel:
                    {
                        int vi = VesselFields.IndexOf(name);
                        if (vi >= 0) return r.AsVessel(vi);
                        if (q.HasModules) return r.AsModule(name);
                        if (q.HasParts && r.TryPart(name)) return r;
                        throw Unknown(name, pointer, "vessel");
                    }
                case Aggregate.PerPart:
                    if (r.TryPart(name)) return r;
                    if (q.HasModules) return r.AsModule(name);
                    {
                        int vi = VesselFields.IndexOf(name);
                        if (vi >= 0) return r.AsVessel(vi);
                    }
                    throw Unknown(name, pointer, "part");
                default:
                    return r.AsModule(name);
            }
        }

        private FieldRef AsVessel(int idx) { Source = FieldSource.Vessel; Index = idx; Kind = VesselFields.All[idx].Kind; return this; }
        private FieldRef AsModule(string f) { Source = FieldSource.Module; ModuleField = f; Kind = FieldKind.Null; return this; }

        private bool TryPart(string name)
        {
            int pi = PartFields.IndexOf(name);
            if (pi >= 0) { Source = FieldSource.Part; Index = pi; Kind = PartFields.All[pi].Kind; return true; }
            if (PartFields.TryParseResource(name, out string res, out bool max)) { Source = FieldSource.Resource; Resource = res; Max = max; Kind = FieldKind.Number; return true; }
            return false;
        }

        private static SkizzyError Unknown(string name, string pointer, string what)
        {
            var names = new List<string>();
            foreach (var f in VesselFields.All) names.Add(f.Name);
            foreach (var f in PartFields.All) names.Add("part." + f.Name);
            string guess = Levenshtein.Closest(name, names);
            return SkizzyError.InvalidParam(pointer, "unknown " + what + " field '" + CompactText.SafeName(name) + "'" + (guess != null ? " (did you mean '" + guess + "'?)" : ""),
                "Vessel fields are listed in the ksp.query description; module fields need select.modules (see ksp.module.describe).");
        }
    }

    // ---------------------------------------------------------------- accessors

    public sealed class VesselFieldAccessor : IFieldAccessor
    {
        private readonly GVessel _v;
        private readonly int _idx;
        public VesselFieldAccessor(GVessel v, int idx) { _v = v; _idx = idx; }
        // skizzy:hot VesselFieldAccessor.Read
        public bool Read(ref FieldValue val) { return _v.IsAlive && _v.ReadField(_idx, ref val); }
    }

    public sealed class PartFieldAccessor : IFieldAccessor
    {
        private readonly GPart _p;
        private readonly int _idx;
        public PartFieldAccessor(GPart p, int idx) { _p = p; _idx = idx; }
        public bool Read(ref FieldValue val) { return _p.ReadField(_idx, ref val); }
    }

    public sealed class ResourceAccessor : IFieldAccessor
    {
        private readonly GPart _p;
        private readonly string _res;
        private readonly bool _max;
        public ResourceAccessor(GPart p, string res, bool max) { _p = p; _res = res; _max = max; }
        public bool Read(ref FieldValue val) { return _p.ReadResource(_res, _max, ref val); }
    }

    /// <summary>One output row: identity plus one source per field (single accessor or several to reduce).</summary>
    public sealed class QueryRow
    {
        public GVessel Vessel;
        public GPart Part;
        public GModule Module;
        public IFieldAccessor[] Single;
        public IFieldAccessor[][] Many;
        public FieldValue[] Values;
        public FieldValue[] Last;     // on-change comparisons
        public bool HasLast;
    }

    /// <summary>
    /// Compiled selector: rows with accessors, rebuilt only when the world's topology changes. Reading is
    /// allocation-free (acceptance 4 / 02 §2.3).
    /// </summary>
    public sealed class QueryPlan
    {
        public readonly QuerySpec Spec;
        private readonly HostCore _host;
        public readonly List<QueryRow> Rows = new List<QueryRow>();
        public int BuiltForTopology = int.MinValue;
        public int BuiltForEpoch = int.MinValue;
        public readonly HashSet<string> UnknownModuleFields = new HashSet<string>();
        private readonly List<GVessel> _vessels = new List<GVessel>();
        private readonly List<IFieldAccessor> _tmp = new List<IFieldAccessor>();
        public int RebuildCount;
        public string[] Columns;

        public QueryPlan(HostCore host, QuerySpec spec)
        {
            _host = host;
            Spec = spec;
            var cols = new List<string> { "vessel", "vesselName" };
            if (spec.Aggregate != Aggregate.PerVessel) cols.Add("part");
            if (spec.Aggregate == Aggregate.PerModule) cols.Add("module");
            cols.AddRange(spec.Fields);
            Columns = cols.ToArray();
        }

        public int IdentityColumns => Spec.Aggregate == Aggregate.PerVessel ? 2 : Spec.Aggregate == Aggregate.PerPart ? 3 : 4;

        /// <summary>Main thread: rebuilds when vessels/parts changed. Returns true if rebuilt.</summary>
        public bool EnsureCurrent()
        {
            var w = _host.World;
            if (w.TopologyVersion == BuiltForTopology && w.SceneEpoch == BuiltForEpoch) return false;
            Build();
            return true;
        }

        public void Build()
        {
            var w = _host.World;
            BuiltForTopology = w.TopologyVersion;
            BuiltForEpoch = w.SceneEpoch;
            RebuildCount++;
            Rows.Clear();
            UnknownModuleFields.Clear();
            _vessels.Clear();
            if (Spec.Handles != null && Spec.Handles.Count > 0)
            {
                foreach (var h in Spec.Handles)
                {
                    try { _vessels.Add(_host.Handles.ResolveVessel(h, "/select/vessels/handles")); }
                    catch (SkizzyError) { if (RebuildCount == 1) throw; }
                }
            }
            else
            {
                w.GetVessels(_vessels);
                for (int i = _vessels.Count - 1; i >= 0; i--) if (!Spec.VesselMatches(_vessels[i])) _vessels.RemoveAt(i);
            }
            var refs = Spec.Refs;
            var moduleFieldSeen = new bool[refs.Length];
            foreach (var v in _vessels)
            {
                if (Spec.Aggregate == Aggregate.PerVessel)
                {
                    var row = NewRow(v, null, null, refs.Length);
                    for (int f = 0; f < refs.Length; f++)
                    {
                        var r = refs[f];
                        if (r.Source == FieldSource.Vessel) { row.Single[f] = new VesselFieldAccessor(v, r.Index); continue; }
                        _tmp.Clear();
                        ForEachPart(v, (p, m) =>
                        {
                            var a = Accessor(r, p, m);
                            if (a != null) { _tmp.Add(a); moduleFieldSeen[f] = true; }
                        }, r.Source == FieldSource.Module);
                        row.Many[f] = _tmp.ToArray();
                    }
                    Rows.Add(row);
                }
                else if (Spec.Aggregate == Aggregate.PerPart)
                {
                    int n = v.LoadedPartCount;
                    for (int i = 0; i < n; i++)
                    {
                        var p = v.GetPart(i);
                        if (!Spec.PartMatches(p)) continue;
                        if (Spec.HasModules && !PartHasMatchingModule(p)) continue;
                        var row = NewRow(v, p, null, refs.Length);
                        for (int f = 0; f < refs.Length; f++)
                        {
                            var r = refs[f];
                            if (r.Source == FieldSource.Module)
                            {
                                _tmp.Clear();
                                for (int m = 0; m < p.ModuleCount; m++)
                                {
                                    var mod = p.GetModule(m);
                                    if (!Spec.ModuleMatches(mod)) continue;
                                    var a = mod.CreateAccessor(r.ModuleField);
                                    if (a != null) { _tmp.Add(a); moduleFieldSeen[f] = true; }
                                }
                                row.Many[f] = _tmp.ToArray();
                            }
                            else row.Single[f] = Accessor(r, p, null);
                        }
                        Rows.Add(row);
                    }
                }
                else
                {
                    int n = v.LoadedPartCount;
                    for (int i = 0; i < n; i++)
                    {
                        var p = v.GetPart(i);
                        if (!Spec.PartMatches(p)) continue;
                        for (int m = 0; m < p.ModuleCount; m++)
                        {
                            var mod = p.GetModule(m);
                            if (!Spec.ModuleMatches(mod)) continue;
                            var row = NewRow(v, p, mod, refs.Length);
                            for (int f = 0; f < refs.Length; f++)
                            {
                                row.Single[f] = Accessor(refs[f], p, mod);
                                if (row.Single[f] != null && refs[f].Source == FieldSource.Module) moduleFieldSeen[f] = true;
                            }
                            Rows.Add(row);
                        }
                    }
                }
            }
            for (int f = 0; f < refs.Length; f++)
                if (refs[f].Source == FieldSource.Module && !moduleFieldSeen[f] && Rows.Count > 0) UnknownModuleFields.Add(refs[f].Name);
        }

        /// <summary>Perf gates: duplicates rows (sharing accessors) until there are at least <paramref name="n"/>.</summary>
        public void RepeatRowsTo(int n)
        {
            int baseCount = Rows.Count;
            if (baseCount == 0) return;
            for (int i = 0; Rows.Count < n; i++)
            {
                var src = Rows[i % baseCount];
                Rows.Add(new QueryRow
                {
                    Vessel = src.Vessel, Part = src.Part, Module = src.Module, Single = src.Single, Many = src.Many,
                    Values = new FieldValue[src.Values.Length], Last = new FieldValue[src.Values.Length],
                });
            }
        }

        private bool PartHasMatchingModule(GPart p)
        {
            for (int m = 0; m < p.ModuleCount; m++) if (Spec.ModuleMatches(p.GetModule(m))) return true;
            return false;
        }

        private void ForEachPart(GVessel v, Action<GPart, GModule> visit, bool modules)
        {
            int n = v.LoadedPartCount;
            for (int i = 0; i < n; i++)
            {
                var p = v.GetPart(i);
                if (!Spec.PartMatches(p)) continue;
                if (!modules) { visit(p, null); continue; }
                for (int m = 0; m < p.ModuleCount; m++)
                {
                    var mod = p.GetModule(m);
                    if (Spec.ModuleMatches(mod)) visit(p, mod);
                }
            }
        }

        private static IFieldAccessor Accessor(FieldRef r, GPart p, GModule m)
        {
            switch (r.Source)
            {
                case FieldSource.Vessel: return new VesselFieldAccessor(p.Vessel, r.Index);
                case FieldSource.Part: return new PartFieldAccessor(p, r.Index);
                case FieldSource.Resource: return new ResourceAccessor(p, r.Resource, r.Max);
                default: return m != null ? m.CreateAccessor(r.ModuleField) : null;
            }
        }

        private static QueryRow NewRow(GVessel v, GPart p, GModule m, int n)
        {
            return new QueryRow
            {
                Vessel = v,
                Part = p,
                Module = m,
                Single = new IFieldAccessor[n],
                Many = new IFieldAccessor[n][],
                Values = new FieldValue[n],
                Last = new FieldValue[n],
            };
        }

        /// <summary>Reads every field of a row into <c>row.Values</c>. Returns false if the vessel is gone.</summary>
        // skizzy:hot QueryPlan.ReadRow
        public bool ReadRow(QueryRow row)
        {
            if (!row.Vessel.IsAlive) return false;
            var vals = row.Values;
            for (int f = 0; f < vals.Length; f++)
            {
                var single = row.Single[f];
                if (single != null)
                {
                    if (!single.Read(ref vals[f])) vals[f].SetNull();
                    continue;
                }
                var many = row.Many[f];
                if (many == null || many.Length == 0) { vals[f].SetNull(); continue; }
                ReduceInto(many, ref vals[f]);
            }
            return true;
        }

        // skizzy:hot QueryPlan.ReduceInto
        private void ReduceInto(IFieldAccessor[] many, ref FieldValue into)
        {
            var tmp = default(FieldValue);
            if (Spec.Reduce == Reduce.First)
            {
                for (int i = 0; i < many.Length; i++)
                    if (many[i].Read(ref tmp) && tmp.Kind != FieldKind.Null) { into = tmp; return; }
                into.SetNull();
                return;
            }
            double acc = 0;
            int n = 0;
            for (int i = 0; i < many.Length; i++)
            {
                if (!many[i].Read(ref tmp)) continue;
                if (!(tmp.IsNumeric || tmp.Kind == FieldKind.Bool)) continue;
                double x = tmp.AsNumber;
                if (double.IsNaN(x)) continue;
                if (n == 0) acc = x;
                else
                {
                    switch (Spec.Reduce)
                    {
                        case Reduce.Min: if (x < acc) acc = x; break;
                        case Reduce.Max: if (x > acc) acc = x; break;
                        default: acc += x; break;
                    }
                }
                n++;
            }
            if (n == 0) { into.SetNull(); return; }
            into.SetNum(Spec.Reduce == Reduce.Avg ? acc / n : acc);
        }

        // ---------------------------------------------------------------- output

        public void WriteIdentity(JsonWriter w, QueryRow row)
        {
            w.String(row.Vessel.Handle).String(row.Vessel.Name);
            if (Spec.Aggregate != Aggregate.PerVessel) w.String(row.Part.Handle);
            if (Spec.Aggregate == Aggregate.PerModule) w.String(row.Module.Handle);
        }

        // skizzy:hot QueryPlan.WriteValue
        public static void WriteValue(JsonWriter w, ref FieldValue v)
        {
            switch (v.Kind)
            {
                case FieldKind.Bool: w.Bool(v.Bool); break;
                case FieldKind.Number:
                    if (NumberFormat.IsFinite(v.Num)) w.Number(v.Num); else w.Null();
                    break;
                case FieldKind.Single:
                    if (NumberFormat.IsFinite(v.Num)) w.Single((float)v.Num); else w.Null();
                    break;
                case FieldKind.Integer: w.Number((long)v.Num); break;
                case FieldKind.String: w.String(v.Str); break;
                default: w.Null(); break;
            }
        }

        public static void AppendCompact(StringBuilder sb, ref FieldValue v)
        {
            switch (v.Kind)
            {
                case FieldKind.Bool: sb.Append(v.Bool ? "true" : "false"); break;
                case FieldKind.Number:
                case FieldKind.Single:
                    sb.Append(CompactNum(v.Num));
                    break;
                case FieldKind.Integer: sb.Append(((long)v.Num).ToString(System.Globalization.CultureInfo.InvariantCulture)); break;
                case FieldKind.String: sb.Append(Quote(v.Str)); break;
                default: sb.Append('-'); break;
            }
        }

        public static string CompactNum(double x)
        {
            if (double.IsNaN(x)) return "NaN";
            if (double.IsInfinity(x)) return x > 0 ? "+Inf" : "-Inf";
            double a = Math.Abs(x);
            string fmt = a >= 1000 ? "0.#" : a >= 1 ? "0.###" : a == 0 ? "0" : "0.#####";
            return x.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture);
        }

        public static string Quote(string s)
        {
            s = CompactText.SafeName(s);
            return s.IndexOf(' ') >= 0 || s.Length == 0 ? "\"" + s.Replace("\"", "'") + "\"" : s;
        }
    }
}
