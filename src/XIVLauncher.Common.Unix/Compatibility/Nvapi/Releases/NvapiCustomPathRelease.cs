namespace XIVLauncher.Common.Unix.Compatibility.Nvapi.Releases;

public sealed class NvapiCustomPathRelease : IToolRelease
{
    public string Name => NvapiManager.CUSTOM_PATH_NAME;
    public string Label => "自定义路径";
    public string Description => $"从 {CustomDirectory} 使用自定义 Nvapi";
    public string DownloadUrl => string.Empty;
    public string Checksum => "skip";

    public string CustomDirectory { get; }

    public NvapiCustomPathRelease(string customDirectory)
    {
        CustomDirectory = customDirectory;
    }
}
