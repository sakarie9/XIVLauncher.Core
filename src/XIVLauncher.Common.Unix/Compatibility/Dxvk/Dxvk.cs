using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using Serilog;

using XIVLauncher.Common.Util;
using XIVLauncher.Common.Unix.Compatibility.Wine;
using XIVLauncher.Common.Unix.Compatibility.Dxvk.Releases;

namespace XIVLauncher.Common.Unix.Compatibility.Dxvk;

public static class Dxvk
{
    public static async Task InstallDxvk(HttpClient httpClient, DirectoryInfo prefix, DirectoryInfo installDirectory, IToolRelease release)
    {
        if (release.Name == "DISABLED")
            return;

        string dxvkPath;

        if (release is DxvkCustomPathRelease customRelease)
        {
            dxvkPath = Path.Combine(customRelease.CustomDirectory, "x64");
            if (!Directory.Exists(dxvkPath))
            {
                Log.Error("Custom DXVK path does not contain x64 directory: {Path}", customRelease.CustomDirectory);
                throw new DirectoryNotFoundException($"x64 directory not found in custom DXVK path: {customRelease.CustomDirectory}");
            }
            Log.Information("Using custom DXVK from {Path}", dxvkPath);
        }
        else
        {
            dxvkPath = Path.Combine(installDirectory.FullName, release.Name, "x64");
            if (!Directory.Exists(dxvkPath))
            {
                Log.Information("DXVK does not exist, downloading");
                await DownloadDxvk(httpClient, installDirectory, release.DownloadUrl, release.Checksum).ConfigureAwait(false);
            }
        }

        var system32 = Path.Combine(prefix.FullName, "drive_c", "windows", "system32");
        var files = Directory.GetFiles(dxvkPath);

        foreach (var fileName in files)
        {
            File.Copy(fileName, Path.Combine(system32, Path.GetFileName(fileName)), true);
        }
    }

    private static async Task DownloadDxvk(HttpClient httpClient, DirectoryInfo installDirectory, string url, string checksum)
    {
        if (string.IsNullOrEmpty(url))
            throw new ArgumentOutOfRangeException("Download URL is null or empty");

        var tempPath = PlatformHelpers.GetTempFileName();

        File.WriteAllBytes(tempPath, await httpClient.GetByteArrayAsync(url).ConfigureAwait(false));

        if (!CompatUtil.EnsureChecksumMatch(tempPath, [checksum]))
            throw new InvalidDataException("SHA512 checksum verification failed");

        PlatformHelpers.Untar(tempPath, installDirectory.FullName);

        File.Delete(tempPath);
    }
}
