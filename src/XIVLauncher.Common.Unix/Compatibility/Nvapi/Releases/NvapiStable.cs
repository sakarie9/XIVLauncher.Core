namespace XIVLauncher.Common.Unix.Compatibility.Nvapi.Releases;

public sealed class NvapiStableRelease : IToolRelease
{
    public string Label { get; } = "Dxvk-Nvapi 0.9.1";
    public string Description { get; } = "For DLSS with nVidia cards and Stable Dxvk release. Does nothing with non-DLSS cards.";
    public string Name { get; } = "dxvk-nvapi-v0.9.1";
    public string DownloadUrl { get; } = "https://github.com/jp7677/dxvk-nvapi/releases/download/v0.9.1/dxvk-nvapi-v0.9.1.tar.gz";
    public string Checksum { get; } = "skip";
}
