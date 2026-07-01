namespace XIVLauncher.Common.Unix.Compatibility.Dxvk.Releases;

public sealed class DxvkStableAsyncRelease : IToolRelease
{
    public string Label { get; } = "GPLAsync 2.7";
    public string Description { get; } = "Dxvk 2.7 with GPLAsync patches. For most graphics cards.";
    public string Name { get; } = "dxvk-gplasync-v2.7-1";
    public string DownloadUrl { get; } = "https://raw.githubusercontent.com/goatcorp/xlcore-distrib/refs/heads/main/dxvk-gplasync-v2.7-1.tar.gz";
    public string Checksum { get; } = "skip";
}
