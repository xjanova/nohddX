namespace NohddX.Core;

public static class Constants
{
    public const string AppName = "NoHddX";
    public const string Version = "1.0.0";

    public const int DefaultBlockSize = 4096;
    public const int DefaultMaxClients = 500;

    public const string CowFileExtension = ".cow";
    public const string BlockMapFileName = "blockmap.bin";
    public const string OverlayFileName = "overlay.cow";

    public static class DhcpOptions
    {
        public const byte ClientArchitecture = 93;
        public const byte VendorClassIdentifier = 60;
        public const string PxeClientIdentifier = "PXEClient";
    }
}
