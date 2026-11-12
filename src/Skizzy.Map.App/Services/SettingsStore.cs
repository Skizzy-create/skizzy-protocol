using System.Text.Json;
using System.Text.Json.Serialization;

namespace Skizzy.Map.App.Services;

/// <summary>Main window placement. Restored only when it still fits on a connected screen.</summary>
public sealed class WindowPlacement
{
    public int X { get; set; }
    public int Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
    /// <summary>Bounds of the screen it was on ("x,y,w,h" in physical pixels), to spot a missing monitor.</summary>
    public string? Screen { get; set; }
}

public sealed class SourceSettings
{
    /// <summary>demo | fake | live</summary>
    public string Kind { get; set; } = "demo";
    /// <summary>Optional endpoint.json path or KSP root for the live host (else the usual discovery).</summary>
    public string? EndpointFile { get; set; }
    public string? KspRoot { get; set; }
}

/// <summary>One map window's persisted settings, saved inside a layout.</summary>
public sealed class MapConfig
{
    public string Title { get; set; } = "Plot";
    public string View { get; set; } = "Blue";
    public double CenterX { get; set; } = 90;
    public double CenterY { get; set; }
    public double Scale { get; set; } = 3;
    public bool Trails { get; set; } = true;
    public bool Altitude { get; set; } = true;
    public bool TargetLines { get; set; }
    public bool Rings { get; set; } = true;
    public bool Labels { get; set; } = true;
    public bool Counts { get; set; }
    public bool Grid { get; set; } = true;
    public bool Zones { get; set; } = true;
    public bool Events { get; set; } = true;
    public bool ScaleBar { get; set; } = true;
    public bool Missiles { get; set; } = true;
    public bool MissileTrails { get; set; } = true;
    public string SizeMode { get; set; } = "Auto";
    public double SizeScale { get; set; } = 1;
    public string PlotFilter { get; set; } = "All";
    public bool Legend { get; set; } = true;
}

/// <summary>A named layout: where every panel and map window is docked, floated or hidden, plus the map windows' settings.</summary>
public sealed class SavedLayout
{
    public int Version { get; set; } = 2;
    public string Name { get; set; } = "";
    public DateTime SavedUtc { get; set; }
    public Skizzy.Map.App.Shell.DockSnapshot? Panels { get; set; }
    public Dictionary<string, MapConfig> Maps { get; set; } = new();
}

public sealed class AppSettings
{
    public int Version { get; set; } = 1;
    public WindowPlacement? Window { get; set; }
    public string Theme { get; set; } = "Dark";
    /// <summary>The layout last chosen by name (shown ticked in the Layouts menu).</summary>
    public string? Layout { get; set; }
    public SourceSettings Source { get; set; } = new();
}

/// <summary>
/// Settings and layouts on disk: %APPDATA%/skizzy/map/settings.json and layouts/&lt;name&gt;.json, plus
/// session-layout.json (the arrangement at last exit). SKIZZY_MAP_HOME overrides the folder (tests, portable use).
/// Corrupt files are moved aside and defaults used; nothing here ever throws into the UI.
/// </summary>
public sealed class SettingsStore
{
    public const string SessionLayoutName = "session-layout";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public SettingsStore(string root)
    {
        Root = root;
        Current = Read<AppSettings>(SettingsPath) ?? new AppSettings();
    }

    public string Root { get; }
    public string SettingsPath => Path.Combine(Root, "settings.json");
    public string LayoutsDir => Path.Combine(Root, "layouts");
    public string SessionLayoutPath => Path.Combine(Root, SessionLayoutName + ".json");

    public AppSettings Current { get; private set; }

    public static string DefaultRoot()
    {
        var env = Environment.GetEnvironmentVariable("SKIZZY_MAP_HOME");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "skizzy", "map");
    }

    public void Save() => Write(SettingsPath, Current);

    public void Reload() => Current = Read<AppSettings>(SettingsPath) ?? new AppSettings();

    // ------------------------------------------------------------ layouts

    public IReadOnlyList<string> ListLayouts()
    {
        if (!Directory.Exists(LayoutsDir)) return [];
        return Directory.EnumerateFiles(LayoutsDir, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => Read<SavedLayout>(Path.Combine(LayoutsDir, n + ".json"))?.Name ?? n!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList()!;
    }

    public void SaveLayout(SavedLayout layout)
    {
        if (string.IsNullOrWhiteSpace(layout.Name)) throw new ArgumentException("A layout needs a name.");
        layout.SavedUtc = DateTime.UtcNow;
        Write(Path.Combine(LayoutsDir, FileName(layout.Name) + ".json"), layout);
    }

    public SavedLayout? LoadLayout(string name) => Read<SavedLayout>(Path.Combine(LayoutsDir, FileName(name) + ".json"));

    public bool DeleteLayout(string name)
    {
        var p = Path.Combine(LayoutsDir, FileName(name) + ".json");
        if (!File.Exists(p)) return false;
        File.Delete(p);
        return true;
    }

    public void SaveSession(SavedLayout layout)
    {
        layout.Name = SessionLayoutName;
        layout.SavedUtc = DateTime.UtcNow;
        Write(SessionLayoutPath, layout);
    }

    public SavedLayout? LoadSession() => Read<SavedLayout>(SessionLayoutPath);

    /// <summary>Layout names become file names: anything outside letters, digits, space, dash and underscore becomes '_'.</summary>
    public static string FileName(string name)
    {
        var chars = name.Trim().Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' ? c : '_').ToArray();
        var s = new string(chars).Trim();
        if (s.Length == 0) s = "layout";
        return s.Length > 64 ? s[..64] : s;
    }

    // ------------------------------------------------------------ io

    private static T? Read<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            try { File.Move(path, path + ".bad-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), overwrite: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return null;
        }
    }

    private static void Write<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine("skizzy-map: could not save " + path + ": " + e.Message);
        }
    }
}
