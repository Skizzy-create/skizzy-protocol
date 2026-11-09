using System;
using BDArmory.Control;
using BDArmory.Competition;
using BDArmory.Targeting;
using BDArmory.Weapons.Missiles;
using Skizzy.C2;
using UnityEngine;

[assembly: KSPAssembly("Skizzy.BDArmory", 0, 1)]
[assembly: KSPAssemblyDependency("BDArmory", 1, 0)]
[assembly: KSPAssemblyDependency("Skizzy.C2", 0, 1)]

namespace Skizzy.Bda.Ksp
{
    /// <summary>
    /// Flight-scene owner of the BDArmory binding: probes the hooks, builds the binding, subscribes to BDArmory's and
    /// KSP's events, and attaches the C2 engine: through the host when it runs (<c>C2Host.Attach</c>, then
    /// <see cref="C2Runtime.Adopt"/>), else <see cref="C2Runtime.Attach"/> (the runtime's own engine, ticked by the C2
    /// toolbar). Detaches, and so restores every setting, when a scene switch is requested, when the host drops the
    /// engine, and again when the scene object is destroyed. A new flight scene re-attaches (new addon instance).
    /// KSP skips this assembly entirely when BDArmory is not installed (KSPAssemblyDependency).
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public sealed class BdaBindingAddon : MonoBehaviour
    {
        private KspBdaGame _game;
        private BdaBinding _binding;
        private HookSet _hooks;
        private bool _subscribed;

        private void Start()
        {
            try
            {
                var typeOf = HookProbe.FromLoadedAssemblies();
                if (typeOf == null || !HookProbe.HasStockCore(typeOf))
                {
                    Debug.LogWarning("[Skizzy.BDArmory] BDArmory not found or too old (no ActiveController/TargetInfo); the C2 engine is not started.");
                    return;
                }
                _hooks = HookProbe.Probe(typeOf);
                string version = null;
                try { version = typeOf(HookProbe.MissileFire).Assembly.GetName().Version.ToString(); }
                catch (Exception) { }
                _game = new KspBdaGame(_hooks, version) { Log = Debug.LogError };
                _binding = new BdaBinding(_game) { Log = Debug.LogError };
                Subscribe();
                var rt = C2Runtime.Current;
                if (rt.LogError == null) rt.LogError = Debug.LogError;
                // One engine: the host's when it runs (c2.* capabilities, 5 Hz slot, order.* events), else the runtime's own.
                IC2EngineHost host = null;
                C2Engine engine = null;
                if (HostBridge.HostLoaded())
                {
                    try { host = HostBridge.TryAttach(_binding, OnHostDetached, out engine); }
                    catch (Exception e)
                    {
                        Debug.LogError("[Skizzy.BDArmory] attaching through the host failed, using the C2 toolbar's own engine: " + e);
                        host = null;
                    }
                }
                if (host != null && engine != null) rt.Adopt(_binding, engine, host);
                else rt.Attach(_binding);
                Debug.Log("[Skizzy.BDArmory] C2 binding attached (" + (host != null ? "host engine" : "own engine, no host") + "): " + _binding.Description);
            }
            catch (Exception e)
            {
                Debug.LogError("[Skizzy.BDArmory] binding start failed: " + e);
                Shutdown();
            }
        }

        private void OnDestroy() { Shutdown(); }

        // The host dropped the engine (scene change, kill of the binding, replaced): restore everything now.
        private void OnHostDetached(string reason)
        {
            var b = _binding;
            if (b == null) return;
            try { C2Runtime.Current.Detach(b); }
            catch (Exception e) { Debug.LogError("[Skizzy.BDArmory] detach after the host dropped the engine failed: " + e); }
            Debug.Log("[Skizzy.BDArmory] the host dropped the C2 engine (" + reason + "); hook settings restored.");
        }

        private void Shutdown()
        {
            if (_subscribed) Unsubscribe();
            if (_binding != null)
            {
                try { C2Runtime.Current.Detach(_binding); } // stale (already detached at the switch) is ignored
                catch (Exception e) { Debug.LogError("[Skizzy.BDArmory] detach failed: " + e); }
                _binding = null;
            }
            _game?.Clear();
            _game = null;
        }

        // ---- events -----------------------------------------------------------------------------

        private bool Has(HookSet h) { return (_hooks & h) != 0; }

        private void Subscribe()
        {
            _subscribed = true;
            GameEvents.onVesselWillDestroy.Add(OnVesselWillDestroy);
            GameEvents.onVesselGoOnRails.Add(OnVesselGoOnRails);
            GameEvents.onVesselUnloaded.Add(OnVesselUnloaded);
            GameEvents.onGameSceneSwitchRequested.Add(OnSceneSwitchRequested);
            if (Has(HookSet.F1)) SubscribeF1();
            if (Has(HookSet.F6)) SubscribeF6();
            if (Has(HookSet.F6b)) SubscribeF6b();
            if (Has(HookSet.F9)) SubscribeF9();
            if (Has(HookSet.F10)) SubscribeF10();
        }

        private void Unsubscribe()
        {
            _subscribed = false;
            GameEvents.onVesselWillDestroy.Remove(OnVesselWillDestroy);
            GameEvents.onVesselGoOnRails.Remove(OnVesselGoOnRails);
            GameEvents.onVesselUnloaded.Remove(OnVesselUnloaded);
            GameEvents.onGameSceneSwitchRequested.Remove(OnSceneSwitchRequested);
            try
            {
                if (Has(HookSet.F1)) UnsubscribeF1();
                if (Has(HookSet.F6)) UnsubscribeF6();
                if (Has(HookSet.F6b)) UnsubscribeF6b();
                if (Has(HookSet.F9)) UnsubscribeF9();
                if (Has(HookSet.F10)) UnsubscribeF10();
            }
            catch (Exception e) { Debug.LogError("[Skizzy.BDArmory] unsubscribe failed: " + e); }
        }

        // One small method per hook so a missing hook's class is never referenced from a method that runs.
        private void SubscribeF1() { HookF1.Subscribe(OnOverrideLost); }
        private void UnsubscribeF1() { HookF1.Unsubscribe(); }
        private void SubscribeF6() { HookF6.Subscribe(OnWaypointReached, OnRouteCompleted); }
        private void UnsubscribeF6() { HookF6.Unsubscribe(); }
        private void SubscribeF6b() { HookF6b.Subscribe(OnPatrolEngage, OnPatrolResume); }
        private void UnsubscribeF6b() { HookF6b.Unsubscribe(); }
        private void SubscribeF9() { HookF9.Subscribe(OnMissileFired); }
        private void UnsubscribeF9() { HookF9.Unsubscribe(); }
        private void SubscribeF10() { HookF10.Subscribe(OnTargetDetected, OnTargetLost, OnDeath); }
        private void UnsubscribeF10() { HookF10.Unsubscribe(); }

        private static uint IdOf(TargetInfo t)
        {
            if (t == null) return 0;
            var v = t.Vessel;
            return v != null ? v.persistentId : 0;
        }

        private static uint IdOf(Vessel v) { return v != null ? v.persistentId : 0; }

        private void OnOverrideLost(MissileFire wm, TargetInfo target, int reason)
        {
            try { _binding?.GameOverrideLost(wm, IdOf(target), reason); }
            catch (Exception e) { Debug.LogError("[Skizzy.BDArmory] override-lost handler: " + e.Message); }
        }

        private void OnWaypointReached(BDGenericAIBase ai, int index, int lap) { _binding?.GameWaypointReached(ai, index, lap); }
        private void OnRouteCompleted(BDGenericAIBase ai) { _binding?.GameRouteCompleted(ai); }
        private void OnPatrolEngage(BDGenericAIBase ai, Vessel target) { _binding?.GamePatrolEngage(ai, IdOf(target)); }
        private void OnPatrolResume(BDGenericAIBase ai, bool leashBroke) { _binding?.GamePatrolResume(ai, leashBroke); }

        private void OnMissileFired(MissileFire wm, MissileBase missile, Vessel target)
        {
            try
            {
                _game?.MarkArmsDirty(wm);
                uint shooter = wm != null ? IdOf(wm.vessel) : 0;
                uint m = missile != null ? IdOf(missile.vessel) : 0;
                if (m == shooter) m = 0; // not yet separated into its own vessel
                _binding?.GameMissileFired(wm, m, IdOf(target));
            }
            catch (Exception e) { Debug.LogError("[Skizzy.BDArmory] missile-fired handler: " + e.Message); }
        }

        private void OnTargetDetected(TargetInfo t, BDTeam team) { if (team != null) _binding?.GameTargetDetected(IdOf(t), team.Name); }
        private void OnTargetLost(TargetInfo t, BDTeam team) { if (team != null) _binding?.GameTargetLost(IdOf(t), team.Name); }
        private void OnDeath(string victim) { _binding?.GameDeath(victim); }

        private void OnVesselWillDestroy(Vessel v) { if (v != null) _binding?.GameVesselDestroyed(v.persistentId); }
        private void OnVesselGoOnRails(Vessel v) { if (v != null) _binding?.GamePacked(v.persistentId); }
        private void OnVesselUnloaded(Vessel v) { if (v != null) _binding?.GameUnloaded(v.persistentId); }

        // Before KSP saves the flight on the way out: put every setting back now.
        private void OnSceneSwitchRequested(GameEvents.FromToAction<GameScenes, GameScenes> _)
        {
            if (_binding == null) return;
            try { C2Runtime.Current.Detach(_binding); }
            catch (Exception e) { Debug.LogError("[Skizzy.BDArmory] detach at scene switch failed: " + e); }
        }
    }
}
