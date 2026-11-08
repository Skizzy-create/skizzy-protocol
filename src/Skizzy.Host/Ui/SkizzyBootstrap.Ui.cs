using System;
using Skizzy.Host.Ui;

namespace Skizzy.Host.Ksp
{
    /// <summary>
    /// The in-game UI seam (21 §1, §3). The UI component is added disabled; it is enabled only when something has to be
    /// seen, so with every window closed Unity calls nothing of it (acceptance 1).
    /// </summary>
    public sealed partial class SkizzyBootstrap
    {
        private SkizzyUi _ui;

        partial void UiAttach()
        {
            try
            {
                _ui = gameObject.AddComponent<SkizzyUi>();
                _ui.enabled = false;
                _ui.Init(this);
            }
            catch (Exception e)
            {
                Log.Warn("in-game UI not available: " + e);
                if (_ui != null)
                {
                    try { _ui.Shutdown(); Destroy(_ui); } catch (Exception) { }
                }
                _ui = null;
            }
        }

        /// <summary>Every frame while the pump is awake: two field reads and a lock-free check unless something must be shown.</summary>
        partial void UiFrame()
        {
            var ui = _ui;
            if (ui == null || ui.enabled) return;
            if (Host.Kill.Engaged || Host.Prompts.Count > 0) ui.enabled = true;
        }

        partial void UiSceneReady(GameScenes scene)
        {
            var ui = _ui;
            if (ui == null) return;
            try { ui.SceneReady(scene); }
            catch (Exception e) { Log.Warn("ui scene hook failed: " + e.Message); }
        }

        partial void UiShutdown()
        {
            var ui = _ui;
            _ui = null;
            if (ui == null) return;
            try { ui.Shutdown(); }
            catch (Exception e) { Log.Warn("ui shutdown failed: " + e.Message); }
        }
    }
}
