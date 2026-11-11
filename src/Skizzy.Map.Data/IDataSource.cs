namespace Skizzy.Map.Data;

public enum SourceKind { Demo, LiveHost, FakeKsp }

public enum SourceState { Idle, Connecting, Online, Reconnecting, Offline, Failed }

[Flags]
public enum SourceCaps
{
    None = 0,
    Pause = 1,
    Speed = 2,
    Restart = 4,
    /// <summary>Has a capability catalog for the command palette.</summary>
    Catalog = 8,
    /// <summary>Takes operator orders through the c2.* contract (<see cref="ICommandSource"/>).</summary>
    Orders = 16,
    /// <summary>The demo battle's side-level controls (posture, advance, friendly fire).</summary>
    DemoControls = 32,
    /// <summary>Accepts typed comms from the operator.</summary>
    Compose = 64,
}

/// <summary>A capability the command palette can show and call.</summary>
public sealed record CatalogEntry(
    string Name,
    string Title,
    string Description,
    string Area,
    string Safety,
    bool Available,
    /// <summary>JSON Schema of the input, as JSON text ("{}" when none).</summary>
    string InputSchemaJson);

/// <summary>
/// Where the map's picture comes from: the built-in demo battle, a live Skizzy host, or an in-process fake
/// KSP host. Sources are driven from the UI thread (<see cref="Tick"/> once per frame) and write into
/// <see cref="MapData"/>; network sources queue what arrives on their read threads and apply it in Tick.
/// </summary>
public interface IDataSource : IDisposable
{
    string Name { get; }
    /// <summary>One line for the brand bar ("Kerbin Strait · air raid on Task Group 1 · sample scenario").</summary>
    string Description { get; }
    SourceKind Kind { get; }
    SourceCaps Caps { get; }
    SourceState State { get; }
    string StatusText { get; }

    bool Paused { get; }
    double Speed { get; }

    /// <summary>Binds to the mirror (resets it) and starts producing data.</summary>
    void Attach(MapData data);

    /// <summary>Advances by <paramref name="realDt"/> seconds of wall time and applies new data. UI thread.</summary>
    void Tick(double realDt);

    void SetPaused(bool paused);
    void SetSpeed(double speed);
    void Restart();

    IReadOnlyList<CatalogEntry> Catalog { get; }

    /// <summary>Calls a capability; returns the result as JSON text. Throws on errors.</summary>
    Task<string> CallAsync(string method, string argsJson, CancellationToken ct);

    HealthSnapshot Health { get; }
}

/// <summary>
/// A source the map can command through the c2.* contract (<see cref="IDataSource.CallAsync"/>): it maps plot points
/// to the lat/lon the contract uses and tracks to C2 unit references.
/// </summary>
public interface ICommandSource
{
    /// <summary>lat/lon (degrees) of a plot point (km east and north of the source's origin). False before the origin is known.</summary>
    bool TryToGeo(double xKm, double yKm, out double lat, out double lon);

    /// <summary>The C2 unit reference of a track ("v:&lt;id&gt;"), or null when the track is not a vessel the engine knows.</summary>
    string? UnitRef(TrackInfo track);

    /// <summary>The side the operator commands when the source knows it (demo: "Blue"); null = ask the engine (c2.unit.status).</summary>
    string? CommandSide { get; }
}
