using System.Collections.Generic;
using Skizzy.Protocol.Permissions;
using Skizzy.Protocol.Schema;

namespace Skizzy.Protocol.Catalog
{
    /// <summary>
    /// M2 capabilities: the track picture engine (tac.*, doc 08) with the missile ledger and engagement log. Executed by
    /// the host's TacticalHost over the BDArmory binding's world (EXECUTOR_UNAVAILABLE "tactical_not_attached" without it).
    /// The bda.* weapons, AI and competition surface is not in this slice; its handlers stay unregistered until the KSP
    /// operations layer exists (HANDOFF.md).
    /// </summary>
    public static class M2Defs
    {
        public const string Toolset = "tactical";
        private const string Mod = "BDArmory";
        private const string Note = " Needs the BDArmory binding in a flight scene (EXECUTOR_UNAVAILABLE tactical_not_attached without it).";
        private static readonly string[] Flight = { "FLIGHT" };

        private static CapabilityDef Def(string name, Safety safety, string desc, SchemaB input, bool list = false)
        {
            return new CapabilityDef
            {
                Name = name,
                Provider = "tac",
                Title = name,
                Description = desc,
                Safety = safety,
                Area = "tactical",
                Mod = Mod,
                Affinity = Affinity.Main,
                Scenes = Flight,
                InputSchema = (input ?? S.Obj().Closed()).Build(),
                Toolsets = new[] { Toolset },
                ListShaped = list,
            };
        }

        private static SchemaB View()
        {
            return S.Str("Picture to read: a team name (your own side unless you are a human, referee or director), or \"god\" for the truth (needs privilege or fog off). Defaults to the session's side.")
                .MinLen(1).MaxLen(64);
        }

        private static SchemaB UnitRef(string desc)
        {
            return S.Str(desc + ": a handle \"v:<id>\", a vessel id or an exact unit name.").Handle("vessel").MinLen(1).MaxLen(256);
        }

        private static SchemaB Units() { return S.Enum("distance units for text: nm (naval: nm, kt, ft) or km (metric)", "nm", "km").Default("nm"); }

        private static SchemaB Limit() { return S.Int("maximum rows (default 10, max 50)").Min(1).Max(50).Default(10); }

        private static SchemaB Format(string def) { return S.Enum("\"compact\" = readable text lines (best for LLMs), \"json\" = structured rows", "compact", "json").Default(def); }

        public static IEnumerable<CapabilityDef> All()
        {
            yield return Def("tac.status", Safety.Read,
                "Track picture engine status: attached or not, teams with track counts, units, fog and name settings, period cost (mean, max, max slice in ms), " +
                "missile ledger counts, snapshot rate and source.tactical subscriptions. Works without the binding (attached:false)." + Note, null);

            yield return Def("tac.configure", Safety.Control,
                "Picture settings for every side (human, referee or director only): fog (IFF unknown until identified), revealNames, dropAfterS for tracks no " +
                "longer held, and sampleHz, the main-thread snapshot rate that drives the picture, the missile ledger and source.tactical (1-10 Hz, default 5)." + Note,
                S.Obj()
                    .Prop("fog", S.Bool("IFF unknown until identified"))
                    .Prop("revealNames", S.Bool("show names of identified hostiles"))
                    .Prop("dropAfterS", S.Num("drop tracks not held for this long").Min(5).Max(600).Unit("s"))
                    .Prop("sampleHz", S.Num("snapshot rate of the picture and missile ledger").Min(1).Max(10).Unit("Hz"))
                    .Closed());

            yield return Def("tac.summary", Safety.Read,
                "One compact paragraph of a side's picture: hostile, unknown and friendly counts, hostile missiles, missiles inbound to own units, the shortest " +
                "time to closest approach, optionally ranged from a reference unit." + Note,
                S.Obj().Prop("view", View()).Prop("ref", UnitRef("reference unit for ranges")).Prop("units", Units()).Closed());

            var filter = S.Obj("Row filter; all conditions must match.")
                .Prop("iff", S.Arr(S.Enum("iff", "hostile", "unknown", "friend", "neutral"), "IFF states").MaxItems(4))
                .Prop("domain", S.Arr(S.Enum("domain", "air", "surface", "subsurface", "missile", "land"), "domains").MaxItems(5))
                .Prop("class", S.Arr(S.Str().MaxLen(32), "unit classes (fighter, ship, missile, ...)").MaxItems(16))
                .Prop("state", S.Arr(S.Enum("track state", "live", "dr", "dead"), "track states (dead tracks are excluded unless asked)").MaxItems(3))
                .Prop("minThreat", S.Num("minimum threat score 0..1").Min(0).Max(1))
                .Prop("maxAgeS", S.Num("maximum seconds since last held").Min(0).Unit("s"))
                .Prop("minQuality", S.Num("minimum track quality 0..1").Min(0).Max(1))
                .Prop("inboundTo", UnitRef("only missiles guiding on this unit"))
                .Prop("within", S.Obj("only tracks within m metres of ref (or of the query's ref)")
                    .Prop("ref", UnitRef("centre unit")).Prop("m", S.Num("radius").Min(1).Unit("m"), true).Closed())
                .Prop("ids", S.Arr(S.Str().Pattern("^T[0-9]+$"), "track numbers like T12").MaxItems(64))
                .Closed();
            yield return Def("tac.query", Safety.Read,
                "Lists tracks of a side's picture: one line each with track number, IFF, class, position or BRAA from ref, speed, threat, engagement. " +
                "Filter by iff, domain, class, state, threat, age, quality, inboundTo, within or ids; sort by threat, range, id or age; page with cursor." + Note,
                S.Obj()
                    .Prop("view", View())
                    .Prop("ref", UnitRef("reference unit for BRAA and range sort"))
                    .Prop("units", Units())
                    .Prop("filter", filter)
                    .Prop("sort", S.Enum("row order", "threat", "range", "id", "age").Default("threat"))
                    .Prop("limit", Limit())
                    .Prop("cursor", S.Str("cursor from a previous result").MaxLen(16))
                    .Prop("format", Format("compact"))
                    .Closed(), true);

            yield return Def("tac.threats", Safety.Read,
                "The ranked threat list of a side (the compact 10-line answer): highest threat first with aspect, closure, range to the threatened unit and " +
                "time to closest approach; optionally only threats to one unit." + Note,
                S.Obj()
                    .Prop("view", View())
                    .Prop("to", UnitRef("only threats to this own unit"))
                    .Prop("units", Units())
                    .Prop("limit", Limit())
                    .Prop("format", Format("compact"))
                    .Closed(), true);

            yield return Def("tac.track.get", Safety.Read,
                "One track in full: identity, IFF and id state, sources, position, kinematics, quality, age, threat, engagement, missile target and BRAA from ref." + Note,
                S.Obj()
                    .Prop("view", View())
                    .Prop("track", S.Str("track number, \"T12\" or \"12\"").MinLen(1).MaxLen(16), true)
                    .Prop("ref", UnitRef("reference unit for BRAA"))
                    .Prop("units", Units())
                    .Closed());

            yield return Def("tac.sitrep", Safety.Read,
                "A short situation report for one own unit: position, weapon manager and missiles, current target as a track number, C2 task and the top threats to it." + Note,
                S.Obj().Prop("unit", UnitRef("own unit"), true).Prop("units", Units()).Closed());

            yield return Def("tac.engagements", Safety.Read,
                "Current engagements in a side's picture: every non-friendly track that is being engaged or has missiles in the air at it, and how many hostiles are unengaged." + Note,
                S.Obj().Prop("view", View()).Closed());

            yield return Def("tac.missiles", Safety.Read,
                "Missiles in flight in a side's picture, most threatening first: track, team or IFF, position and velocity, weapon, launcher, target, state, fired time, cost in KSP funds and time to closest approach." + Note,
                S.Obj().Prop("view", View()).Prop("limit", Limit()).Closed(), true);

            yield return Def("tac.engagement_log", Safety.Read,
                "The missile engagement log and costs: newest first, lines like \"A Hornet-1 fired AIM-120 at Kirov\", \"intercepted by B Kirov SM-2\", \"hit Kirov\", " +
                "\"missed\", plus per-team totals (fired, intercepts made, hits, misses, destroyed) and funds: spent, cost of own missiles intercepted, cost of hits. " +
                "Costs are KSP part costs summed over each missile's parts. Page backwards with before, or poll forwards with since (the last seq you saw)." + Note,
                S.Obj()
                    .Prop("view", View())
                    .Prop("team", S.Str("only entries where this team fired or was fired at").MinLen(1).MaxLen(64))
                    .Prop("since", S.Int("only entries with seq greater than this (polling)").Min(0))
                    .Prop("before", S.Int("only entries with seq less than this (paging back)").Min(1))
                    .Prop("limit", Limit())
                    .Prop("format", Format("compact"))
                    .Closed(), true);
        }
    }
}
