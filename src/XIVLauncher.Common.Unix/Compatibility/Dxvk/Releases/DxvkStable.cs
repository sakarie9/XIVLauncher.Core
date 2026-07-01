namespace XIVLauncher.Common.Unix.Compatibility.Dxvk.Releases;

public sealed class DxvkStableRelease : IToolRelease
{
    public string Label { get; } = "3.0";
    public string Description { get; } = "Dxvk 3.0. Latest stable version.";
    public string Name { get; } = "dxvk-3.0";
    public string DownloadUrl { get; } = "https://github.com/doitsujin/dxvk/releases/download/v3.0/dxvk-3.0.tar.gz";
    public string Checksum { get; } = "skip";
}