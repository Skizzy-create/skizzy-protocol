using System;
using Skizzy.Host.Core.World;
using Skizzy.Protocol.Json;

namespace Skizzy.Host.Core
{
    /// <summary>
    /// Translates game notifications into host state changes and events (03 §7 vessel.*, sim.*; 20 §1–5). Called by
    /// the KSP GameEvents handlers and by FakeKsp, on the main thread, so both behave identically.
    /// </summary>
    public sealed class WorldEvents
    {
        private readonly HostCore _h;

        public WorldEvents(HostCore h) { _h = h; }

        private void Emit(string name, string category, string summary, JsonValue data)
        {
            _h.Events.Emit(name, category, summary, data);
            _h.Touch();
        }

        public void SceneChangeRequested(string from, string to)
        {
            _h.SceneTransition = true;
            _h.RefreshMeta();
            _h.Ops.CancelAll("scene_change");
            Emit("sim.scene", "system", "scene change " + from + " -> " + to + " requested", JsonValue.Obj().Set("from", from).Set("to", to).Set("phase", "requested"));
        }

        public void SceneLoaded(string scene)
        {
            _h.SceneTransition = false;
            _h.RefreshMeta();
            Emit("sim.scene", "system", "scene " + scene + " loaded", JsonValue.Obj().Set("scene", scene).Set("phase", "loaded"));
        }

        public void Paused() { _h.RefreshMeta(); Emit("sim.paused", "system", "game paused", null); }
        public void Resumed() { _h.RefreshMeta(); Emit("sim.resumed", "system", "game resumed", null); }

        public void WarpChanged(string mode, double rate)
        {
            _h.RefreshMeta();
            Emit("sim.warp_changed", "system", "warp " + mode + " x" + Inv.S(rate), JsonValue.Obj().Set("mode", mode).Set("rate", rate));
        }

        public void ActiveVesselChanged(GVessel from, GVessel to)
        {
            _h.RefreshMeta();
            Emit("vessel.active_changed", "unit", "active vessel " + Name(from) + " -> " + Name(to),
                JsonValue.Obj().Set("from", from?.Handle).Set("to", to?.Handle).Set("toName", to?.Name));
        }

        public void VesselCreated(GVessel v)
        {
            if (v == null) return;
            _h.Handles.NoteSeen(v.PersistentId);
            Emit("vessel.created", "unit", Name(v) + " created (" + v.TypeName + ")", Data(v));
        }

        public void VesselDestroyed(uint pid, string name, string handle)
        {
            _h.Handles.NoteDestroyed(pid);
            Emit("vessel.destroyed", "unit", (name ?? handle) + " destroyed", JsonValue.Obj().Set("vessel", handle).Set("name", name));
        }

        public void VesselLoaded(GVessel v) { Emit("vessel.loaded", "unit", Name(v) + " loaded", Data(v)); }
        public void VesselUnloaded(GVessel v) { Emit("vessel.unloaded", "unit", Name(v) + " unloaded", Data(v)); }
        public void VesselPacked(GVessel v) { Emit("vessel.packed", "unit", Name(v) + " went on rails", Data(v)); }
        public void VesselUnpacked(GVessel v) { Emit("vessel.unpacked", "unit", Name(v) + " went off rails", Data(v)); }

        public void VesselRenamed(GVessel v, string from, string to)
        {
            Emit("vessel.renamed", "unit", from + " renamed to " + to, JsonValue.Obj().Set("vessel", v?.Handle).Set("from", from).Set("to", to));
        }

        public void PersistentIdChanged(uint oldPid, uint newPid)
        {
            _h.Handles.NotePidChanged(oldPid, newPid);
            Emit("vessel.pid_changed", "unit", "vessel id " + oldPid + " -> " + newPid,
                JsonValue.Obj().Set("old", HandleRegistry.VesselHandle(oldPid)).Set("new", HandleRegistry.VesselHandle(newPid)));
        }

        public void SituationChanged(GVessel v, string situation)
        {
            Emit("vessel.situation", "unit", Name(v) + " is now " + situation, Data(v).Set("situation", situation));
        }

        private static string Name(GVessel v) { return v == null ? "-" : v.Name; }
        private static JsonValue Data(GVessel v) { return JsonValue.Obj().Set("vessel", v?.Handle).Set("name", v?.Name); }
    }
}
