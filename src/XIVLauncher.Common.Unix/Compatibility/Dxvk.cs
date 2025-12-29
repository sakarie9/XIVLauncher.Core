using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Serilog;
using XIVLauncher.Common.Util;

namespace XIVLauncher.Common.Unix.Compatibility;

public static class Dxvk
{
#if WINE_XIV_MACOS
    // Dxvk from https://softwareupdate.xivmac.com/sites/default/files/update_data/XIV%20on%20Mac5.3.1.tar.xz;
    private const string DXVK_DOWNLOAD = ServerAddress.S3Address + "/xlcore/deps/dxvk/osx/xom-5.3.1/dxvk.tar.gz";
    private const string DXVK_NAME = "dxvk";
#else
    // private const string DXVK_DOWNLOAD = "https://raw.githubusercontent.com/goatcorp/xlcore-distrib/refs/heads/main/dxvk-gplasync-v2.6.1-1.tar.gz";
    private const string DXVK_DOWNLOAD = ServerAddress.S3Address + "/xlcore/deps/dxvk/linux/dxvk-gplasync-v2.6.1-1.tar.gz";
    private const string DXVK_NAME = "dxvk-gplasync-v2.6.1-1";
#endif

    public static async Task InstallDxvk(DirectoryInfo prefix, DirectoryInfo installDirectory)
    {
        var dxvkPath = Path.Combine(installDirectory.FullName, DXVK_NAME, "x64");

        if (!Directory.Exists(dxvkPath))
        {
            Log.Information("DXVK does not exist, downloading");
            await DownloadDxvk(installDirectory).ConfigureAwait(false);
        }

        var system32 = Path.Combine(prefix.FullName, "drive_c", "windows", "system32");
        var files = Directory.GetFiles(dxvkPath);

        Log.Information("Extracting DXVK files");
        foreach (string fileName in files)
        {
            File.Copy(fileName, Path.Combine(system32, Path.GetFileName(fileName)), true);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Log.Information("Copying dxvk cache for Mac OSX");
            File.Copy(
                Path.Combine(Paths.ResourcesPath, "ffxiv_dx11.dxvk-cache-base"),
                Path.Combine(prefix.FullName, "drive_c", "ffxiv_dx11.dxvk-cache"),
                true
            );
        }
    }

    private static async Task DownloadDxvk(DirectoryInfo installDirectory)
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("User-Agent", PlatformHelpers.GetVersion());
        var tempPath = PlatformHelpers.GetTempFileName();

        File.WriteAllBytes(tempPath, await client.GetByteArrayAsync(DXVK_DOWNLOAD));
        PlatformHelpers.Untar(tempPath, installDirectory.FullName);

        File.Delete(tempPath);
    }

    public enum DxvkHudType
    {
        [SettingsDescription("None", "Show nothing")]
        None,

        [SettingsDescription("FPS", "Only show FPS")]
        Fps,

        [SettingsDescription("Full", "Show everything")]
        Full,
    }
}
