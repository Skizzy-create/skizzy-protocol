using System.Globalization;

namespace Skizzy.Map.Data;

/// <summary>
/// One line of the host's missile engagement log (source.tactical "log", tac.engagement_log): fired, hit, intercepted,
/// missed, destroyed or killed. <see cref="T"/> is map time; costs are KSP funds (NaN when unknown).
/// </summary>
public sealed record EngagementEntry(
    long Seq,
    double T,
    string Kind,
    string? Team,
    string? Weapon,
    string? Missile,
    string? LauncherName,
    string? TargetName,
    string? TargetTeam,
    string? OtherName,
    string? OtherTeam,
    double Cost,
    string? Source,
    string Text);

/// <summary>Per-team missile totals (source.tactical "totals"): counts and funds, as the host's ledger keeps them.</summary>
public sealed record EngagementTotals(
    string Team,
    int Fired,
    int InFlight,
    int Hits,
    int Misses,
    int Intercepted,
    int Destroyed,
    int InterceptsMade,
    int Kills,
    double Spent,
    double InterceptedCost,
    double HitCost,
    double MissedCost,
    int CostUnknown);

/// <summary>
/// The map's copy of the host's engagement log and per-team totals. Filled by the live source from the tactical stream;
/// read by the Engagements panel on the UI thread. Bounded (oldest entries drop in blocks).
/// </summary>
public sealed class EngagementBook
{
    private readonly List<EngagementEntry> _entries = new();
    private readonly List<EngagementTotals> _totals = new();

    public EngagementBook(int capacity = 5000) { Capacity = capacity; }

    public int Capacity { get; }
    public IReadOnlyList<EngagementEntry> Entries => _entries;
    public IReadOnlyList<EngagementTotals> Totals => _totals;
    /// <summary>Highest log sequence number held.</summary>
    public long LastSeq { get; private set; }
    /// <summary>Bumps on every change; the panel re-renders when it moves.</summary>
    public long Version { get; private set; }
    /// <summary>Why there is nothing to show (no tactical stream), or null.</summary>
    public string? Note { get; set; }

    /// <summary>Adds an entry unless one with the same or a later sequence number is already held. True when added.</summary>
    public bool Add(EngagementEntry e)
    {
        if (e.Seq <= LastSeq) return false;
        LastSeq = e.Seq;
        _entries.Add(e);
        if (_entries.Count > Capacity) _entries.RemoveRange(0, Math.Max(1, Capacity / 10));
        Version++;
        return true;
    }

    /// <summary>Replaces the totals; bumps the version only when something changed.</summary>
    public void SetTotals(IReadOnlyList<EngagementTotals> totals)
    {
        bool same = totals.Count == _totals.Count;
        for (int i = 0; same && i < totals.Count; i++) same = totals[i] == _totals[i];
        if (same) return;
        _totals.Clear();
        _totals.AddRange(totals);
        Version++;
    }

    public EngagementTotals? TotalsOf(string team)
    {
        foreach (var t in _totals) if (t.Team == team) return t;
        return null;
    }

    public void Clear()
    {
        _entries.Clear();
        _totals.Clear();
        LastSeq = 0;
        Version++;
    }

    public void Touch() => Version++;

    /// <summary>Funds as the panel shows them: "1,250", "—" when unknown.</summary>
    public static string Funds(double v) => double.IsNaN(v) || double.IsInfinity(v) ? "—" : v.ToString("#,0", CultureInfo.InvariantCulture);

    /// <summary>The log category of an entry kind (Messages tab).</summary>
    public static LogCategory CategoryOf(string kind) => kind switch
    {
        "fired" or "missed" => LogCategory.Weapons,
        "intercepted" => LogCategory.Intercept,
        _ => LogCategory.Damage,
    };
}
