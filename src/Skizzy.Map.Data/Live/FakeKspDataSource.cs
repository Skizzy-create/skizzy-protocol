using Skizzy.Client;
using Skizzy.Host.Core;
using Skizzy.Sim;

namespace Skizzy.Map.Data.Live;

/// <summary>
/// The M0 FakeKsp host started in-process on a free loopback port (a temporary folder for its plugin data), with
/// the map connected to it like to a real KSP. For the desktop acceptance run without KSP, and for tests.
/// </summary>
public sealed class FakeKspDataSource : HostDataSource
{
    private readonly SimHost _sim;
    private readonly string _root;

    private FakeKspDataSource(SimHost sim, string root)
        : base(() => new Endpoint { Url = sim.Url, Token = sim.Token }, "FakeKSP", SourceKind.FakeKsp, "FakeKSP · in-process Skizzy host, M0 vessel stream")
    {
        _sim = sim;
        _root = root;
    }

    public SimHost Sim => _sim;

    public static FakeKspDataSource Start(FakeWorld? world = null)
    {
        string root = Path.Combine(Path.GetTempPath(), "skizzy-map-fake-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        var sim = new SimHost(root, world ?? FakeWorld.CreateDefault(), new HostSettings { PortFirst = 0, PortLast = 0 });
        sim.Start();
        return new FakeKspDataSource(sim, root);
    }

    public override void Dispose()
    {
        base.Dispose();
        try { _sim.Dispose(); } catch (ObjectDisposedException) { }
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
