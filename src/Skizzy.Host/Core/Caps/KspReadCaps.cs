using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Skizzy.Host.Core.World;
using Skizzy.Protocol.Catalog;
using Skizzy.Protocol.Errors;
using Skizzy.Protocol.Json;
using Skizzy.Protocol.Util;

namespace Skizzy.Host.Core.Caps
{
    /// <summary>ksp.* read capabilities on top of <see cref="IGameWorld"/> (17 M0 "Reading").</summary>
    public static class KspReadCaps
    {
        public static void Register(HostCore h)
        {
            h.Register("ksp.vessels.list", VesselsList);
            h.Register("ksp.vessel.get", VesselGet);
            h.Register("ksp.query", Query);
            h.Register("ksp.module.describe", ModuleDescribe);
            h.Register("ksp.module.get", ModuleGet);
            h.Register("ksp.mods.list", ModsList);
            h.Register("ksp.log.tail", LogTail);
        }

        private static readonly int FSituation = VesselFields.IndexOf("situation");
        private static readonly int FBody = VesselFields.IndexOf("body");
        private static readonly int FLat = VesselFields.IndexOf("lat");
        private static readonly int FLon = VesselFields.IndexOf("lon");
        private static readonly int FAlt = VesselFields.IndexOf("altitude");
        private static readonly int FSpeed = VesselFields.IndexOf("srfSpeed");
        private static readonly int FCrew = VesselFields.IndexOf("crewCount");
        private static readonly int FParts = VesselFields.IndexOf("partCount");

        internal static int ParseCursor(string cursor)
        {
            if (string.IsNullOrEmpty(cursor)) return 0;
            if (cursor.StartsWith("o:", StringComparison.Ordinal) && int.TryParse(cursor.Substring(2), NumberStyles.None, CultureInfo.InvariantCulture, out int o)) return o;
            throw SkizzyError.InvalidParam("/cursor", "invalid cursor; pass the cursor from a previous result");
        }

        private static JsonValue Field(GVessel v, int idx)
        {
            var fv = default(FieldValue);
            if (idx < 0 || !v.ReadField(idx, ref fv)) return JsonValue.Null;
            return fv.ToJson();
        }

        private static string State(GVessel v) { return !v.Loaded ? "unloaded" : v.Packed ? "packed" : "live"; }

        // ---------------------------------------------------------------- ksp.vessels.list

        private static JsonValue VesselsList(CallContext c)
        {
            var h = c.Host;
            var all = new List<GVessel>();
            h.World.GetVessels(all);
            bool includeDebris = c.Bool("includeDebris", false);
            string nameLike = c.Str("nameLike");
            var types = c.Params.Get("type");
            HashSet<string> typeSet = null;
            if (types != null && types.IsString) typeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { types.AsString() };
            else if (types != null && types.IsArray) { typeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase); foreach (var t in types.Items) typeSet.Add(t.AsString()); }
            var filtered = new List<GVessel>();
            foreach (var v in all)
            {
                if (!v.IsAlive) continue;
                if (c.Has("loaded") && v.Loaded != c.Bool("loaded")) continue;
                if (!includeDebris && v.IsDebris && (typeSet == null || !typeSet.Contains(v.TypeName))) continue;
                if (nameLike != null && !Glob.IsMatch(v.Name, nameLike)) continue;
                if (typeSet != null && !typeSet.Contains(v.TypeName)) continue;
                filtered.Add(v);
            }
            // Active first, then loaded, then by name: stable paging.
            filtered.Sort((a, b) =>
            {
                if (a.IsActive != b.IsActive) return a.IsActive ? -1 : 1;
                if (a.Loaded != b.Loaded) return a.Loaded ? -1 : 1;
                int n = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                return n != 0 ? n : a.PersistentId.CompareTo(b.PersistentId);
            });
            int offset = ParseCursor(c.Str("cursor"));
            int limit = c.Int("limit", 50);
            int end = Math.Min(filtered.Count, offset + limit);
            bool truncated = end < filtered.Count;
            string next = truncated ? "o:" + end : null;
            bool compact = c.Str("format", "json") == "compact";
            var meta = h.Meta;
            if (compact)
            {
                var sb = new StringBuilder();
                sb.Append("#vessels scene=").Append(meta.Scene).Append(" n=").Append(end - offset).Append(" total=").Append(filtered.Count);
                if (truncated) sb.Append(" cursor=").Append(next);
                sb.Append("\nhandle name type situation body alt_m srfSpeed_mps state parts crew\n");
                for (int i = offset; i < end; i++)
                {
                    var v = filtered[i];
                    h.Handles.NoteSeen(v.PersistentId);
                    sb.Append(v.IsActive ? "*" : "").Append(v.Handle).Append(' ').Append(QueryPlan.Quote(v.Name)).Append(' ').Append(v.TypeName).Append(' ');
                    AppendField(sb, v, FSituation); sb.Append(' ');
                    AppendField(sb, v, FBody); sb.Append(' ');
                    AppendField(sb, v, FAlt); sb.Append(' ');
                    AppendField(sb, v, FSpeed); sb.Append(' ');
                    sb.Append(State(v)).Append(' ').Append(v.PartCount).Append(' ');
                    AppendField(sb, v, FCrew);
                    sb.Append('\n');
                }
                if (filtered.Count > 0) sb.Append("(* = active vessel)\n");
                return JsonValue.Obj().Set("text", sb.ToString()).Set("total", filtered.Count).Set("truncated", truncated).Set("cursor", next);
            }
            var arr = JsonValue.Arr();
            for (int i = offset; i < end; i++)
            {
                var v = filtered[i];
                h.Handles.NoteSeen(v.PersistentId);
                arr.Add(JsonValue.Obj()
                    .Set("handle", v.Handle).Set("name", v.Name).Set("type", v.TypeName)
                    .Set("situation", Field(v, FSituation)).Set("body", Field(v, FBody))
                    .Set("lat", Field(v, FLat)).Set("lon", Field(v, FLon)).Set("altitude", Field(v, FAlt))
                    .Set("state", State(v)).Set("parts", v.PartCount).Set("crew", Field(v, FCrew))
                    .Set("srfSpeed", Field(v, FSpeed)).Set("active", v.IsActive));
            }
            return JsonValue.Obj().Set("vessels", arr).Set("total", filtered.Count).Set("truncated", truncated).Set("cursor", next);
        }

        private static void AppendField(StringBuilder sb, GVessel v, int idx)
        {
            var fv = default(FieldValue);
            if (idx < 0 || !v.ReadField(idx, ref fv)) { sb.Append('-'); return; }
            QueryPlan.AppendCompact(sb, ref fv);
        }

        // ---------------------------------------------------------------- ksp.vessel.get

        private static readonly string[] SummaryFields =
        {
            "type", "situation", "body", "lat", "lon", "altitude", "heightFromTerrain", "srfSpeed", "obtSpeed", "verticalSpeed",
            "heading", "pitch", "roll", "course", "mach", "geeForce", "mass", "partCount", "crewCount", "currentStage", "missionTime",
            "loaded", "packed", "landed", "splashed", "isActive", "controllable", "ctrlState.mainThrottle",
            "actionGroups.sas", "actionGroups.rcs", "actionGroups.gear", "actionGroups.brakes", "actionGroups.lights",
        };

        private static JsonValue VesselGet(CallContext c)
        {
            var h = c.Host;
            var v = h.Handles.ResolveVessel(c.Str("vessel", "active"));
            int depth = c.Int("depth", 0);
            int maxParts = c.Int("maxParts", 200);
            var fields = JsonValue.Obj();
            foreach (var f in SummaryFields)
            {
                int idx = VesselFields.IndexOf(f);
                var fv = default(FieldValue);
                if (idx >= 0 && v.ReadField(idx, ref fv)) fields.Set(f, fv.ToJson());
            }
            bool bdWm = v.Loaded && v.HasModule("MissileFire");
            bool bdAi = v.Loaded && (v.HasModule("BDModulePilotAI") || v.HasModule("BDModuleSurfaceAI") || v.HasModule("BDModuleVTOLAI") || v.HasModule("BDModuleOrbitalAI"));
            var bda = JsonValue.Obj().Set("weaponManager", bdWm).Set("ai", bdAi);
            JsonValue parts = null;
            int listed = 0;
            if (depth >= 1 && v.Loaded)
            {
                parts = JsonValue.Arr();
                int n = v.LoadedPartCount;
                for (int i = 0; i < n && listed < maxParts; i++, listed++)
                {
                    var p = v.GetPart(i);
                    var po = JsonValue.Obj().Set("handle", p.Handle).Set("name", p.Name).Set("title", p.Title);
                    var mods = JsonValue.Arr();
                    for (int m = 0; m < p.ModuleCount; m++)
                    {
                        var mod = p.GetModule(m);
                        if (depth >= 2) mods.Add(JsonValue.Obj().Set("handle", mod.Handle).Set("class", mod.ClassName));
                        else mods.Add(mod.ClassName);
                    }
                    po.Set("modules", mods);
                    parts.Add(po);
                }
            }
            if (c.Str("format", "json") == "compact")
            {
                var sb = new StringBuilder();
                sb.Append("#vessel ").Append(v.Handle).Append(' ').Append(QueryPlan.Quote(v.Name)).Append(v.IsActive ? " (active)" : "").Append('\n');
                int col = 0;
                foreach (var f in SummaryFields)
                {
                    var val = fields.Get(f);
                    if (val == null || val.IsNull) continue;
                    sb.Append(f).Append('=');
                    if (val.IsNumber) sb.Append(QueryPlan.CompactNum(val.AsDouble()));
                    else if (val.IsString) sb.Append(QueryPlan.Quote(val.AsString()));
                    else sb.Append(val.ToString());
                    sb.Append(++col % 6 == 0 ? '\n' : ' ');
                }
                if (col % 6 != 0) sb.Append('\n');
                sb.Append("bda: weaponManager=").Append(bdWm ? "yes" : "no").Append(" ai=").Append(bdAi ? "yes" : "no").Append('\n');
                if (parts != null)
                {
                    sb.Append("parts (").Append(listed).Append(" of ").Append(v.PartCount).Append("):\n");
                    foreach (var po in parts.Items)
                    {
                        sb.Append(po.GetString("handle")).Append(' ').Append(QueryPlan.Quote(po.GetString("title"))).Append(" [");
                        var mods = po.GetArr("modules");
                        for (int i = 0; i < mods.Count; i++)
                        {
                            if (i > 0) sb.Append(' ');
                            sb.Append(mods[i].IsString ? mods[i].AsString() : mods[i].GetString("handle"));
                        }
                        sb.Append("]\n");
                    }
                }
                return JsonValue.Obj().Set("text", sb.ToString()).Set("handle", v.Handle);
            }
            var res = JsonValue.Obj().Set("handle", v.Handle).Set("name", v.Name).Set("state", State(v)).Set("fields", fields).Set("bda", bda);
            if (parts != null) res.Set("parts", parts).Set("partsListed", listed).Set("partsTruncated", listed < v.PartCount);
            if (depth >= 1 && !v.Loaded) res.Set("note", "vessel is not loaded; parts are only available for loaded vessels");
            return res;
        }

        // ---------------------------------------------------------------- ksp.query

        private static JsonValue Query(CallContext c)
        {
            var h = c.Host;
            var spec = QuerySpec.FromParams(c.Params);
            var plan = new QueryPlan(h, spec);
            plan.Build();
            int offset = ParseCursor(c.Str("cursor"));
            int limit = c.Int("limit", 50);
            var rowsOut = new List<QueryRow>();
            int total = 0;
            foreach (var r in plan.Rows)
            {
                if (!plan.ReadRow(r)) continue;
                if (total >= offset && rowsOut.Count < limit) rowsOut.Add(r);
                total++;
            }
            bool truncated = offset + rowsOut.Count < total;
            string next = truncated ? "o:" + (offset + rowsOut.Count) : null;
            var warnings = new List<string>();
            foreach (var f in plan.UnknownModuleFields) warnings.Add("no selected module has a field '" + f + "' (see ksp.module.describe)");
            if (c.Str("format", "json") == "compact")
            {
                var sb = new StringBuilder();
                sb.Append("#query rows=").Append(rowsOut.Count).Append(" total=").Append(total);
                if (truncated) sb.Append(" cursor=").Append(next);
                sb.Append('\n').Append(string.Join(" ", plan.Columns)).Append('\n');
                foreach (var r in rowsOut)
                {
                    sb.Append(r.Vessel.Handle).Append(' ').Append(QueryPlan.Quote(r.Vessel.Name));
                    if (r.Part != null) sb.Append(' ').Append(r.Part.Handle);
                    if (r.Module != null) sb.Append(' ').Append(r.Module.Handle);
                    for (int f = 0; f < r.Values.Length; f++) { sb.Append(' '); QueryPlan.AppendCompact(sb, ref r.Values[f]); }
                    sb.Append('\n');
                }
                foreach (var w in warnings) sb.Append("! ").Append(w).Append('\n');
                return JsonValue.Obj().Set("text", sb.ToString()).Set("total", total).Set("truncated", truncated).Set("cursor", next);
            }
            var rows = JsonValue.Arr();
            foreach (var r in rowsOut)
            {
                var row = JsonValue.Arr().Add(r.Vessel.Handle).Add(r.Vessel.Name);
                if (r.Part != null) row.Add(r.Part.Handle);
                if (r.Module != null) row.Add(r.Module.Handle);
                for (int f = 0; f < r.Values.Length; f++) row.Add(r.Values[f].ToJson());
                rows.Add(row);
            }
            var res = JsonValue.Obj().Set("columns", JsonValue.ArrOf(plan.Columns)).Set("rows", rows).Set("total", total)
                .Set("truncated", truncated).Set("cursor", next);
            if (warnings.Count > 0) res.Set("warnings", JsonValue.ArrOf(warnings.ToArray()));
            return res;
        }

        // ---------------------------------------------------------------- modules

        private static JsonValue FieldInfoJson(ModuleFieldInfo f)
        {
            var o = JsonValue.Obj().Set("name", f.Name).Set("guiName", f.GuiName).Set("type", f.JsonType).Set("clrType", f.ValueType)
                .Set("units", string.IsNullOrEmpty(f.Units) ? null : f.Units).Set("shownInFlight", f.ShownInFlight).Set("shownInEditor", f.ShownInEditor)
                .Set("writable", f.Writable);
            if (f.Min.HasValue) o.Set("minimum", f.Min.Value);
            if (f.Max.HasValue) o.Set("maximum", f.Max.Value);
            if (f.Step.HasValue) o.Set("step", f.Step.Value);
            if (f.Options != null) o.Set("enum", JsonValue.ArrOf(f.Options));
            return o;
        }

        private static JsonValue ModuleDescribe(CallContext c)
        {
            var h = c.Host;
            ModuleTypeInfo info;
            if (c.Has("module"))
            {
                var m = h.Handles.ResolveModule(c.Str("module"));
                info = new ModuleTypeInfo { ClassName = m.ClassName, Fields = m.Fields, Events = m.Events, Actions = m.Actions };
            }
            else if (c.Has("type"))
            {
                info = h.World.DescribeModuleType(c.Str("type"));
                if (info == null) throw SkizzyError.NotFound(c.Str("type"), "unknown", "No loaded part module class with that name (class names are case-sensitive, e.g. ModuleEngines).");
            }
            else throw SkizzyError.InvalidParams("give type or module", null, "Pass type (class name) or module (handle).");
            var fields = JsonValue.Arr();
            foreach (var f in info.Fields) fields.Add(FieldInfoJson(f));
            return JsonValue.Obj().Set("class", info.ClassName).Set("assembly", info.Assembly).Set("fields", fields)
                .Set("events", JsonValue.ArrOf(ToArr(info.Events))).Set("actions", JsonValue.ArrOf(ToArr(info.Actions)));
        }

        private static string[] ToArr(IList<string> l)
        {
            if (l == null) return new string[0];
            var a = new string[l.Count];
            l.CopyTo(a, 0);
            return a;
        }

        private static JsonValue ModuleGet(CallContext c)
        {
            var h = c.Host;
            var m = h.Handles.ResolveModule(c.Str("module"));
            var want = c.Params.GetStringList("fields");
            var values = JsonValue.Obj();
            var unknown = new List<string>();
            if (want == null)
            {
                want = new List<string>();
                foreach (var f in m.Fields) want.Add(f.Name);
            }
            foreach (var name in want)
            {
                var acc = m.CreateAccessor(name);
                if (acc == null) { unknown.Add(name); continue; }
                var fv = default(FieldValue);
                values.Set(name, acc.Read(ref fv) ? fv.ToJson() : JsonValue.Null);
            }
            var res = JsonValue.Obj().Set("module", m.Handle).Set("class", m.ClassName).Set("part", m.Part.Handle).Set("vessel", m.Part.Vessel.Handle)
                .Set("values", values);
            if (unknown.Count > 0) res.Set("unknownFields", JsonValue.ArrOf(unknown.ToArray()));
            return res;
        }

        private static JsonValue ModsList(CallContext c)
        {
            var mods = new List<ModInfo>();
            c.Host.World.GetMods(mods);
            string like = c.Str("nameLike");
            var filtered = new List<ModInfo>();
            foreach (var m in mods) if (like == null || Glob.IsMatch(m.Name, like) || (m.Folder != null && Glob.IsMatch(m.Folder, like))) filtered.Add(m);
            int offset = ParseCursor(c.Str("cursor"));
            int limit = c.Int("limit", 100);
            int end = Math.Min(filtered.Count, offset + limit);
            var arr = JsonValue.Arr();
            for (int i = offset; i < end; i++)
            {
                var m = filtered[i];
                arr.Add(JsonValue.Obj().Set("name", m.Name).Set("kind", m.Kind).Set("version", m.Version).Set("folder", m.Folder).Set("file", m.File));
            }
            return JsonValue.Obj().Set("mods", arr).Set("total", filtered.Count).Set("truncated", end < filtered.Count).Set("cursor", end < filtered.Count ? "o:" + end : null);
        }

        // ---------------------------------------------------------------- ksp.log.tail (any thread)

        private static JsonValue LogTail(CallContext c)
        {
            string path = c.Host.World.LogPath;
            if (path == null || !File.Exists(path)) throw SkizzyError.NotFound("KSP.log", "unknown", "KSP.log was not found next to the game executable.");
            int lines = c.Int("lines", 50);
            string level = c.Str("level", "all");
            string contains = c.Str("contains");
            var keep = new List<string>();
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long size = fs.Length;
                int chunk = (int)Math.Min(size, 2 * 1024 * 1024);
                fs.Position = size - chunk;
                var buf = new byte[chunk];
                int got = 0;
                while (got < chunk)
                {
                    int r = fs.Read(buf, got, chunk - got);
                    if (r <= 0) break;
                    got += r;
                }
                var all = Encoding.UTF8.GetString(buf, 0, got).Split('\n');
                for (int i = all.Length - 1; i >= (size > chunk ? 1 : 0) && keep.Count < lines; i--)
                {
                    string l = all[i].TrimEnd('\r');
                    if (l.Length == 0) continue;
                    if (level == "warning" && !(l.StartsWith("[WRN", StringComparison.Ordinal) || l.StartsWith("[ERR", StringComparison.Ordinal) || l.StartsWith("[EXC", StringComparison.Ordinal))) continue;
                    if (level == "error" && !(l.StartsWith("[ERR", StringComparison.Ordinal) || l.StartsWith("[EXC", StringComparison.Ordinal))) continue;
                    if (contains != null && l.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    keep.Add(l.Length > 2000 ? l.Substring(0, 2000) + "..." : l);
                }
            }
            keep.Reverse();
            var sb = new StringBuilder();
            foreach (var l in keep) sb.Append(l).Append('\n');
            return JsonValue.Obj().Set("text", sb.ToString()).Set("lines", keep.Count).Set("path", "KSP.log");
        }
    }
}
