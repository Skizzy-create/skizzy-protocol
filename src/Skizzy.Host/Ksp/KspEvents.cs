using System;
using System.Collections.Generic;
using Skizzy.Host.Core;

namespace Skizzy.Host.Ksp
{
    /// <summary>
    /// The one place Skizzy subscribes to <c>GameEvents</c> (20 §8): scene events for the plugin's lifetime, vessel/time
    /// events only while the pump is awake (a client, a log, a stream, an op or the grace period). Every handler goes
    /// through here so <c>host.selfcheck</c> can list them and a leak shows as a rising count.
    /// </summary>
    internal sealed class KspEvents
    {
        private readonly HostCore _h;
        private readonly KspWorld _w;
        private readonly IHostLog _log;
        private readonly Dictionary<string, int> _counts = new Dictionary<string, int>();
        private bool _sceneOn, _dynamicOn;
        private KspVessel _lastActive;
        private GameScenes _lastScene = GameScenes.LOADING;
        internal long Fired;

        // Delegates are created once so Add and Remove see the same instances.
        private readonly EventData<GameScenes>.OnEvent _sceneRequested, _sceneReady;
        private readonly EventVoid.OnEvent _pause, _unpause, _warp;
        private readonly EventData<Vessel>.OnEvent _create, _destroy, _willDestroy, _change, _loaded, _unloaded, _onRails, _offRails, _modified, _partCount;
        private readonly EventData<GameEvents.HostedFromToAction<Vessel, string>>.OnEvent _rename;
        private readonly EventData<GameEvents.HostedFromToAction<Vessel, Vessel.Situations>>.OnEvent _situation;
        private readonly EventData<uint, uint>.OnEvent _pidChanged;

        /// <summary>Raised on the main thread after the world has been told about a scene change (bootstrap hooks).</summary>
        internal Action SceneChanging;
        /// <summary>Raised on the main thread when a scene's GUI is ready, after the world and host know (UI hook).</summary>
        internal Action<GameScenes> SceneReady;
        private EventVoid.OnEvent _launcherReady, _launcherDestroyed;

        public KspEvents(HostCore h, KspWorld w, IHostLog log)
        {
            _h = h;
            _w = w;
            _log = log;
            _sceneRequested = OnSceneRequested;
            _sceneReady = OnSceneReady;
            _pause = () => Guard("pause", () => _h.WorldEvents.Paused());
            _unpause = () => Guard("unpause", () => _h.WorldEvents.Resumed());
            _warp = OnWarp;
            _create = v => Guard("create", () => { _w.BumpTopology(); _h.WorldEvents.VesselCreated(_w.Wrap(v)); });
            _destroy = v => Guard("destroy", () => _w.Forget(v));
            _willDestroy = OnWillDestroy;
            _change = OnActiveChanged;
            _loaded = v => Guard("loaded", () => { _w.BumpTopology(); _h.WorldEvents.VesselLoaded(_w.Wrap(v)); });
            _unloaded = v => Guard("unloaded", () => { _w.BumpTopology(); _h.WorldEvents.VesselUnloaded(_w.Wrap(v)); });
            _onRails = v => Guard("rails", () => { _w.BumpTopology(); _h.WorldEvents.VesselPacked(_w.Wrap(v)); });
            _offRails = v => Guard("offrails", () => { _w.BumpTopology(); _h.WorldEvents.VesselUnpacked(_w.Wrap(v)); });
            _modified = v => _w.BumpTopology();
            _partCount = v => _w.BumpTopology();
            _rename = a => Guard("rename", () => _h.WorldEvents.VesselRenamed(_w.Wrap(a.host), a.from, a.to));
            _situation = a => Guard("situation", () => _h.WorldEvents.SituationChanged(_w.Wrap(a.host), KspNames.Situation(a.to)));
            _pidChanged = (o, n) => Guard("pid", () => { _w.BumpTopology(); _h.WorldEvents.PersistentIdChanged(o, n); });
        }

        internal bool DynamicOn => _dynamicOn;

        /// <summary>Registered handlers by event (self-check, 20 §8).</summary>
        internal Dictionary<string, int> Counts { get { return new Dictionary<string, int>(_counts); } }

        internal int Total
        {
            get
            {
                int n = 0;
                foreach (var kv in _counts) n += kv.Value;
                return n;
            }
        }

        private void Count(string name, int delta)
        {
            _counts.TryGetValue(name, out int n);
            n += delta;
            if (n == 0) _counts.Remove(name); else _counts[name] = n;
        }

        internal void AddScene()
        {
            if (_sceneOn) return;
            GameEvents.onGameSceneLoadRequested.Add(_sceneRequested); Count("onGameSceneLoadRequested", 1);
            GameEvents.onLevelWasLoadedGUIReady.Add(_sceneReady); Count("onLevelWasLoadedGUIReady", 1);
            _sceneOn = true;
        }

        internal void RemoveScene()
        {
            if (!_sceneOn) return;
            GameEvents.onGameSceneLoadRequested.Remove(_sceneRequested); Count("onGameSceneLoadRequested", -1);
            GameEvents.onLevelWasLoadedGUIReady.Remove(_sceneReady); Count("onLevelWasLoadedGUIReady", -1);
            _sceneOn = false;
        }

        internal void AddDynamic()
        {
            if (_dynamicOn) return;
            GameEvents.onGamePause.Add(_pause); Count("onGamePause", 1);
            GameEvents.onGameUnpause.Add(_unpause); Count("onGameUnpause", 1);
            GameEvents.onTimeWarpRateChanged.Add(_warp); Count("onTimeWarpRateChanged", 1);
            GameEvents.onVesselCreate.Add(_create); Count("onVesselCreate", 1);
            GameEvents.onVesselDestroy.Add(_destroy); Count("onVesselDestroy", 1);
            GameEvents.onVesselWillDestroy.Add(_willDestroy); Count("onVesselWillDestroy", 1);
            GameEvents.onVesselChange.Add(_change); Count("onVesselChange", 1);
            GameEvents.onVesselLoaded.Add(_loaded); Count("onVesselLoaded", 1);
            GameEvents.onVesselUnloaded.Add(_unloaded); Count("onVesselUnloaded", 1);
            GameEvents.onVesselGoOnRails.Add(_onRails); Count("onVesselGoOnRails", 1);
            GameEvents.onVesselGoOffRails.Add(_offRails); Count("onVesselGoOffRails", 1);
            GameEvents.onVesselWasModified.Add(_modified); Count("onVesselWasModified", 1);
            GameEvents.onVesselPartCountChanged.Add(_partCount); Count("onVesselPartCountChanged", 1);
            GameEvents.onVesselRename.Add(_rename); Count("onVesselRename", 1);
            GameEvents.onVesselSituationChange.Add(_situation); Count("onVesselSituationChange", 1);
            GameEvents.onVesselPersistentIdChanged.Add(_pidChanged); Count("onVesselPersistentIdChanged", 1);
            _dynamicOn = true;
            // Events were not observed while asleep: force query plans to rebuild and re-read the active vessel.
            _w.BumpTopology();
            _lastActive = _w.ActiveVessel as KspVessel;
        }

        internal void RemoveDynamic()
        {
            if (!_dynamicOn) return;
            GameEvents.onGamePause.Remove(_pause); Count("onGamePause", -1);
            GameEvents.onGameUnpause.Remove(_unpause); Count("onGameUnpause", -1);
            GameEvents.onTimeWarpRateChanged.Remove(_warp); Count("onTimeWarpRateChanged", -1);
            GameEvents.onVesselCreate.Remove(_create); Count("onVesselCreate", -1);
            GameEvents.onVesselDestroy.Remove(_destroy); Count("onVesselDestroy", -1);
            GameEvents.onVesselWillDestroy.Remove(_willDestroy); Count("onVesselWillDestroy", -1);
            GameEvents.onVesselChange.Remove(_change); Count("onVesselChange", -1);
            GameEvents.onVesselLoaded.Remove(_loaded); Count("onVesselLoaded", -1);
            GameEvents.onVesselUnloaded.Remove(_unloaded); Count("onVesselUnloaded", -1);
            GameEvents.onVesselGoOnRails.Remove(_onRails); Count("onVesselGoOnRails", -1);
            GameEvents.onVesselGoOffRails.Remove(_offRails); Count("onVesselGoOffRails", -1);
            GameEvents.onVesselWasModified.Remove(_modified); Count("onVesselWasModified", -1);
            GameEvents.onVesselPartCountChanged.Remove(_partCount); Count("onVesselPartCountChanged", -1);
            GameEvents.onVesselRename.Remove(_rename); Count("onVesselRename", -1);
            GameEvents.onVesselSituationChange.Remove(_situation); Count("onVesselSituationChange", -1);
            GameEvents.onVesselPersistentIdChanged.Remove(_pidChanged); Count("onVesselPersistentIdChanged", -1);
            _dynamicOn = false;
            _lastActive = null;
        }

        private void Guard(string what, Action a)
        {
            Fired++;
            try { a(); }
            catch (Exception e) { _log.Warn("GameEvents handler " + what + " failed: " + e.Message); }
        }

        private void OnSceneRequested(GameScenes to)
        {
            Guard("scene requested", () =>
            {
                string from = KspWorld.SceneName(HighLogic.LoadedScene);
                _w.OnSceneChanging();
                _lastActive = null;
                SceneChanging?.Invoke();
                _h.WorldEvents.SceneChangeRequested(from, KspWorld.SceneName(to));
            });
        }

        private void OnSceneReady(GameScenes scene)
        {
            Guard("scene ready", () =>
            {
                _w.OnSceneReady();
                _lastScene = scene;
                _h.WorldEvents.SceneLoaded(KspWorld.SceneName(scene));
                if (_dynamicOn) _lastActive = _w.ActiveVessel as KspVessel;
                SceneReady?.Invoke(scene);
            });
        }

        internal bool LauncherOn => _launcherReady != null;

        /// <summary>Toolbar button lifetime events for the in-game UI (counted like every other handler).</summary>
        internal void AddLauncher(EventVoid.OnEvent ready, EventVoid.OnEvent destroyed)
        {
            if (_launcherReady != null || ready == null || destroyed == null) return;
            _launcherReady = ready;
            _launcherDestroyed = destroyed;
            GameEvents.onGUIApplicationLauncherReady.Add(ready); Count("onGUIApplicationLauncherReady", 1);
            GameEvents.onGUIApplicationLauncherDestroyed.Add(destroyed); Count("onGUIApplicationLauncherDestroyed", 1);
        }

        internal void RemoveLauncher()
        {
            if (_launcherReady == null) return;
            GameEvents.onGUIApplicationLauncherReady.Remove(_launcherReady); Count("onGUIApplicationLauncherReady", -1);
            GameEvents.onGUIApplicationLauncherDestroyed.Remove(_launcherDestroyed); Count("onGUIApplicationLauncherDestroyed", -1);
            _launcherReady = _launcherDestroyed = null;
        }

        internal GameScenes LastScene => _lastScene;

        private void OnWarp()
        {
            Guard("warp", () =>
            {
                float rate = TimeWarp.CurrentRate;
                string mode = rate <= 1.0001f ? "none" : TimeWarp.WarpMode == TimeWarp.Modes.HIGH ? "rails" : "physics";
                _h.WorldEvents.WarpChanged(mode, rate);
            });
        }

        private void OnWillDestroy(Vessel v)
        {
            Guard("will destroy", () =>
            {
                if (v == null) return;
                var w = _w.Wrap(v);
                string name = v.vesselName, handle = w.Handle;
                uint pid = v.persistentId;
                _w.Forget(v);
                _h.WorldEvents.VesselDestroyed(pid, name, handle);
            });
        }

        private void OnActiveChanged(Vessel v)
        {
            Guard("active changed", () =>
            {
                var to = _w.Wrap(v);
                var from = _lastActive;
                _lastActive = to;
                if (ReferenceEquals(from, to)) return;
                _h.WorldEvents.ActiveVesselChanged(from, to);
            });
        }
    }
}
