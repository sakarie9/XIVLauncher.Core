namespace XIVLauncher.Common.Unix.Compatibility.Dxvk.Releases;

public sealed class DxvkCustomPathRelease : IToolRelease
{
    public string Name => DxvkManager.CUSTOM_PATH_NAME;
    public string Label => "自定义路径";
    public string Description => $"从 {CustomDirectory} 使用自定义 DXVK";
    public string DownloadUrl => string.Empty;
    public string Checksum => "skip";

    public string CustomDirectory { get; }

    public DxvkCustomPathRelease(string customDirectory)
    {
        CustomDirectory = customDirectory;
    }
}
