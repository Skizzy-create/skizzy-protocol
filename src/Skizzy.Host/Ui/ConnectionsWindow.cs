using System;
using System.Collections.Generic;
using System.IO;
using Skizzy.Host.Core;
using Skizzy.Protocol.Json;
using UnityEngine;

namespace Skizzy.Host.Ui
{
    /// <summary>
    /// Connections (21 §3, M0): sessions with actor and client (incl. the MCP client name), per-session byte rates,
    /// disconnect, and token regeneration with an in-page confirmation. The token itself is never shown.
    /// </summary>
    internal sealed class ConnectionsWindow : UiWindow
    {
        private sealed class Row
        {
            internal string Id, Client, Actor, Detail, Rate;
            internal bool Attached;
        }

        private readonly List<Session> _sessions = new List<Session>();
        private readonly List<Row> _rows = new List<Row>();
        private readonly Dictionary<string, long> _lastBytes = new Dictionary<string, long>();
        private float _nextRefresh, _lastRefresh;
        private Vector2 _scroll;
        private bool _confirmRegen;
        private string _status;

        internal ConnectionsWindow(SkizzyUi ui, int index) : base(ui, index, "Connections", 60, 120, 560, 200) { }

        internal override void OnOpened() { _nextRefresh = 0; _confirmRegen = false; _status = null; }

        internal override void OnLayout()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextRefresh) return;
            float dt = _lastRefresh > 0 ? now - _lastRefresh : 0;
            _lastRefresh = now;
            _nextRefresh = now + 1f;
            _sessions.Clear();
            Ui.Host.SnapshotSessions(_sessions);
            _rows.Clear();
            var seen = new HashSet<string>();
            for (int i = 0; i < _sessions.Count; i++)
            {
                var s = _sessions[i];
                JsonValue d;
                try { d = s.Describe(); }
                catch (Exception) { continue; }
                var client = d.GetObj("client");
                var actor = d.GetObj("actor");
                long bin = d.GetLong("bytesIn"), bout = d.GetLong("bytesOut");
                long total = bin + bout;
                string rate = "";
                if (_lastBytes.TryGetValue(s.Id, out long prev) && dt > 0.01f) rate = UiKit.Bytes((long)((total - prev) / dt)) + "/s";
                _lastBytes[s.Id] = total;
                seen.Add(s.Id);
                string mcp = client != null ? client.GetString("mcpClient") : null;
                _rows.Add(new Row
                {
                    Id = s.Id,
                    Attached = d.GetBool("attached"),
                    Client = (client != null ? client.GetString("name", "?") + " " + client.GetString("version", "") + " [" + client.GetString("kind", "?") + "]" : "?") +
                             (string.IsNullOrEmpty(mcp) ? "" : "  via " + mcp),
                    Actor = actor != null ? (actor.GetString("display") ?? actor.GetString("id") ?? "?") + "  kind " + actor.GetString("kind", "?") + ", role " + actor.GetString("role", "?") +
                            (actor.GetString("side") != null ? ", side " + actor.GetString("side") : "") + (d.GetBool("seat") ? ", seat holder" : "") : "?",
                    Detail = "age " + UiKit.I(d.GetLong("ageS")) + " s, calls " + UiKit.I(d.GetLong("calls")) + ", errors " + UiKit.I(d.GetLong("errors")) +
                             ", in " + UiKit.Bytes(bin) + ", out " + UiKit.Bytes(bout),
                    Rate = rate,
                });
            }
            if (_lastBytes.Count > seen.Count + 16)
            {
                var stale = new List<string>();
                foreach (var k in _lastBytes.Keys) if (!seen.Contains(k)) stale.Add(k);
                foreach (var k in stale) _lastBytes.Remove(k);
            }
        }

        protected override void Body()
        {
            var h = Ui.Host;
            UiKit.KeyValue("Listener", h.Port > 0 ? "ws://127.0.0.1:" + h.Port + " (loopback only)" : (h.StartError ?? "not listening"));
            UiKit.KeyValue("Token file", h.Paths.Relative(h.Paths.TokenFile) + "  (contents never shown)");
            UiKit.KeyValue("Endpoint file", h.Paths.Relative(h.Paths.EndpointFile));

            UiKit.Section("Sessions (" + _rows.Count + ")");
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(Math.Min(360, 40 + _rows.Count * 92)));
            if (_rows.Count == 0) GUILayout.Label("Nothing is connected. Skizzy costs nothing until something connects.", UiKit.Grey);
            for (int i = 0; i < _rows.Count; i++)
            {
                var r = _rows[i];
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.BeginHorizontal();
                GUILayout.Label(r.Client, UiKit.Bold);
                GUILayout.FlexibleSpace();
                GUILayout.Label(r.Attached ? "attached" : "detached (resume window)", r.Attached ? UiKit.Green : UiKit.Yellow);
                GUILayout.EndHorizontal();
                GUILayout.Label(r.Actor, UiKit.Wrap);
                GUILayout.Label(r.Detail + (r.Rate.Length > 0 ? ", " + r.Rate : "") + "   " + r.Id, UiKit.Small);
                if (r.Attached)
                {
                    string id = r.Id;
                    UiKit.Btn("Disconnect", () =>
                    {
                        Ui.Host.Disconnect(id, "disconnected by the human in game");
                        _status = "Disconnected " + id + ".";
                        _nextRefresh = 0;
                    }, GUILayout.Width(110));
                }
                GUILayout.EndVertical();
            }
            GUILayout.EndScrollView();

            UiKit.Section("Token");
            if (!_confirmRegen)
                UiKit.Btn("Regenerate token...", () => _confirmRegen = true, GUILayout.Width(180));
            else
            {
                GUILayout.Label("Regenerating the token disconnects every client; they reconnect by reading the new token file. Continue?", UiKit.Yellow);
                GUILayout.BeginHorizontal();
                UiKit.Btn("Yes, regenerate", () =>
                {
                    _confirmRegen = false;
                    try
                    {
                        Ui.Host.RegenerateToken();
                        _status = "New token written to " + Ui.Host.Paths.Relative(Ui.Host.Paths.TokenFile) + "; all clients were disconnected.";
                    }
                    catch (Exception e) { _status = "Could not regenerate the token: " + e.Message; }
                    _nextRefresh = 0;
                }, GUILayout.Width(160));
                UiKit.Btn("Cancel", () => _confirmRegen = false, GUILayout.Width(90));
                GUILayout.EndHorizontal();
            }
            UiKit.Btn("Copy token file path", () => UiKit.Copy(Path.GetFullPath(Ui.Host.Paths.TokenFile)), GUILayout.Width(180));
            if (_status != null) GUILayout.Label(_status, UiKit.Wrap);
        }
    }
}
