using System;
using System.Collections.Generic;
using System.Text;

namespace Skizzy.Protocol.Config
{
    /// <summary>
    /// Unity-free reader/writer for KSP ConfigNode syntax (one "key = value" per line, "NAME { ... }" blocks,
    /// "//" comments). Used for permissions.cfg and settings.cfg so the files look like every other KSP config
    /// (PLAN §6.4) while the parsing logic stays testable in CI.
    /// </summary>
    public sealed class CfgNode
    {
        public string Name;
        public readonly List<KeyValuePair<string, string>> Values = new List<KeyValuePair<string, string>>();
        public readonly List<CfgNode> Nodes = new List<CfgNode>();

        public CfgNode(string name = "root") { Name = name; }

        public string GetValue(string key, string def = null)
        {
            for (int i = 0; i < Values.Count; i++) if (string.Equals(Values[i].Key, key, StringComparison.OrdinalIgnoreCase)) return Values[i].Value;
            return def;
        }

        public List<string> GetValues(string key)
        {
            var r = new List<string>();
            for (int i = 0; i < Values.Count; i++) if (string.Equals(Values[i].Key, key, StringComparison.OrdinalIgnoreCase)) r.Add(Values[i].Value);
            return r;
        }

        public bool HasValue(string key) { return GetValue(key) != null; }

        public CfgNode GetNode(string name)
        {
            for (int i = 0; i < Nodes.Count; i++) if (string.Equals(Nodes[i].Name, name, StringComparison.OrdinalIgnoreCase)) return Nodes[i];
            return null;
        }

        public List<CfgNode> GetNodes(string name)
        {
            var r = new List<CfgNode>();
            for (int i = 0; i < Nodes.Count; i++) if (string.Equals(Nodes[i].Name, name, StringComparison.OrdinalIgnoreCase)) r.Add(Nodes[i]);
            return r;
        }

        public CfgNode AddValue(string key, string value)
        {
            Values.Add(new KeyValuePair<string, string>(key, value ?? ""));
            return this;
        }

        public CfgNode SetValue(string key, string value)
        {
            for (int i = 0; i < Values.Count; i++)
            {
                if (string.Equals(Values[i].Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    Values[i] = new KeyValuePair<string, string>(Values[i].Key, value ?? "");
                    return this;
                }
            }
            return AddValue(key, value);
        }

        public CfgNode AddNode(string name)
        {
            var n = new CfgNode(name);
            Nodes.Add(n);
            return n;
        }

        public CfgNode AddNode(CfgNode n)
        {
            Nodes.Add(n);
            return n;
        }

        public bool GetBool(string key, bool def)
        {
            var v = GetValue(key);
            if (v == null) return def;
            v = v.Trim();
            if (v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1" || v.Equals("yes", StringComparison.OrdinalIgnoreCase)) return true;
            if (v.Equals("false", StringComparison.OrdinalIgnoreCase) || v == "0" || v.Equals("no", StringComparison.OrdinalIgnoreCase)) return false;
            return def;
        }

        public int GetInt(string key, int def)
        {
            var v = GetValue(key);
            return v != null && int.TryParse(v.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int r) ? r : def;
        }

        public double GetDouble(string key, double def)
        {
            var v = GetValue(key);
            return v != null && double.TryParse(v.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double r) ? r : def;
        }

        /// <summary>Parses text that contains one or more top-level nodes; returns a synthetic root holding them.</summary>
        public static CfgNode Parse(string text)
        {
            var root = new CfgNode("root");
            var stack = new Stack<CfgNode>();
            stack.Push(root);
            string pendingName = null;
            var lines = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int ln = 0; ln < lines.Length; ln++)
            {
                string line = StripComment(lines[ln]).Trim();
                while (line.Length > 0)
                {
                    int eq = line.IndexOf('=');
                    int ob = line.IndexOf('{');
                    int cb = line.IndexOf('}');
                    if (eq >= 0 && (ob < 0 || eq < ob) && (cb < 0 || eq < cb))
                    {
                        // key = value (value runs to end of line, or to a '}' that closes a one-line block)
                        string key = line.Substring(0, eq).Trim();
                        string rest = line.Substring(eq + 1);
                        int close = rest.IndexOf('}');
                        string value = close >= 0 ? rest.Substring(0, close) : rest;
                        if (pendingName != null) { stack.Peek().AddValue(pendingName, ""); pendingName = null; }
                        if (key.Length > 0) stack.Peek().AddValue(key, value.Trim());
                        line = close >= 0 ? rest.Substring(close).Trim() : "";
                        continue;
                    }
                    if (ob >= 0 && (cb < 0 || ob < cb))
                    {
                        string name = line.Substring(0, ob).Trim();
                        if (name.Length == 0) name = pendingName ?? "";
                        pendingName = null;
                        var n = stack.Peek().AddNode(name);
                        stack.Push(n);
                        line = line.Substring(ob + 1).Trim();
                        continue;
                    }
                    if (cb >= 0)
                    {
                        string before = line.Substring(0, cb).Trim();
                        if (before.Length > 0 && pendingName == null) pendingName = before;
                        if (pendingName != null) { stack.Peek().AddValue(pendingName, ""); pendingName = null; }
                        if (stack.Count > 1) stack.Pop();
                        line = line.Substring(cb + 1).Trim();
                        continue;
                    }
                    // A bare word: the name of a node whose '{' is on the next line.
                    if (pendingName != null) stack.Peek().AddValue(pendingName, "");
                    pendingName = line;
                    line = "";
                }
            }
            return root;
        }

        private static string StripComment(string line)
        {
            int i = line.IndexOf("//", StringComparison.Ordinal);
            return i >= 0 ? line.Substring(0, i) : line;
        }

        public string Serialize()
        {
            var sb = new StringBuilder();
            Write(sb, 0);
            return sb.ToString();
        }

        /// <summary>Writes this node's children (values and nodes) without a wrapper, e.g. for the synthetic root.</summary>
        public string SerializeChildren()
        {
            var sb = new StringBuilder();
            WriteBody(sb, 0);
            return sb.ToString();
        }

        private void Write(StringBuilder sb, int indent)
        {
            Indent(sb, indent);
            sb.Append(Name).Append('\n');
            Indent(sb, indent);
            sb.Append("{\n");
            WriteBody(sb, indent + 1);
            Indent(sb, indent);
            sb.Append("}\n");
        }

        private void WriteBody(StringBuilder sb, int indent)
        {
            foreach (var kv in Values)
            {
                Indent(sb, indent);
                sb.Append(kv.Key).Append(" = ").Append(kv.Value).Append('\n');
            }
            foreach (var n in Nodes) n.Write(sb, indent);
        }

        private static void Indent(StringBuilder sb, int n)
        {
            for (int i = 0; i < n; i++) sb.Append("    ");
        }
    }
}
