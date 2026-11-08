using System;

namespace Skizzy.C2.Fake
{
    /// <summary>An <see cref="IC2Binding"/> over <see cref="FakeBda"/>, for runtime and UI-model tests.</summary>
    public sealed class FakeBinding : IC2Binding
    {
        public readonly FakeBda World;
        public int ReleaseAllCalls;
        public IHookEvents Sink => World.Sink;

        public FakeBinding(FakeBda world = null) { World = world ?? new FakeBda(); }

        IC2World IC2Binding.World => World;
        public IBdaControl Control => World;
        public string Description => "FakeBda, hooks " + World.Hooks;

        public void Connect(IHookEvents sink) { World.Sink = sink; }

        public void ReleaseAll() { ReleaseAllCalls++; }
    }
}
