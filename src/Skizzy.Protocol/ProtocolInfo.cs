namespace Skizzy.Protocol
{
    /// <summary>Wire constants shared by the host, the bridge, the CLI and the SDKs.</summary>
    public static class ProtocolInfo
    {
        public const string Name = "skizzy-protocol";
        public const string Version = "1.0";
        public const string SubProtocol = "skizzy.v1";
        public const string Path = "/skizzy";
        public const int DefaultPort = 47549;
        public const int PortRangeEnd = 47559;
        public const string HostVersion = "0.1.0";
        public static readonly string[] SupportedVersions = { "1.0" };
    }
}
