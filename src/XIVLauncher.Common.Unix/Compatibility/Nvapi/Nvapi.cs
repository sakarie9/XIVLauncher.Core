using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Collections.Generic;

using Serilog;

using XIVLauncher.Common.Unix.Compatibility.Wine;
using XIVLauncher.Common.Unix.Compatibility.Nvapi.Releases;
using XIVLauncher.Common.Util;

namespace XIVLauncher.Common.Unix.Compatibility.Nvapi;

public static class Nvapi
{
    public static async Task InstallNvapi(HttpClient httpClient, DirectoryInfo prefix, DirectoryInfo installDirectory, IToolRelease release)
    {
        if (release.Name == "DISABLED")
            return;

        string nvapiPath;

        if (release is NvapiCustomPathRelease customRelease)
        {
            nvapiPath = Path.Combine(customRelease.CustomDirectory, "x64");
            if (!Directory.Exists(nvapiPath))
            {
                Log.Error("Custom Nvapi path does not contain x64 directory: {Path}", customRelease.CustomDirectory);
                throw new DirectoryNotFoundException($"x64 directory not found in custom Nvapi path: {customRelease.CustomDirectory}");
            }
            Log.Information("Using custom Nvapi from {Path}", nvapiPath);
        }
        else
        {
            nvapiPath = Path.Combine(installDirectory.FullName, release.Name, "x64");
            if (!Directory.Exists(nvapiPath))
            {
                var installPath = new DirectoryInfo(Path.Combine(installDirectory.FullName, release.Name));
                if (!installPath.Exists)
                    installPath.Create();
                Log.Information("Dxvk-nvapi does not exist, downloading");
                await DownloadNvapi(httpClient, installPath, release.DownloadUrl, release.Checksum).ConfigureAwait(false);
            }
        }

        var system32 = Path.Combine(prefix.FullName, "drive_c", "windows", "system32");
        var files = Directory.GetFiles(nvapiPath);

        foreach (var fileName in files)
        {
            File.Copy(fileName, Path.Combine(system32, Path.GetFileName(fileName)), true);
        }
    }

    // DLSS support: nvngx.dll must be symlinked into both game dir AND prefix system32
    public static void CopyNvngx(DirectoryInfo gameDirectory, DirectoryInfo prefix, DirectoryInfo storage)
    {
        var nvngxPath = NvidiaWineDLLPath(storage);
        if (string.IsNullOrEmpty(nvngxPath))
        {
            Log.Information("No nvngx.dll or _nvngx.dll found. Try copying them to ~/.xlcore/compatibilitytool");
            Log.Information("If using AMD or intel graphics, ignore this message");
            return;
        }

        var files = Directory.GetFiles(nvngxPath);
        var game = Path.Combine(gameDirectory.FullName, "game");
        var system32 = Path.Combine(prefix.FullName, "drive_c", "windows", "system32");

        foreach (var file in files)
        {
            var source = new FileInfo(file);
            CreateSymlink(source, new FileInfo(Path.Combine(game, source.Name)));
            CreateSymlink(source, new FileInfo(Path.Combine(system32, source.Name)));
        }
    }

    private static void CreateSymlink(FileInfo source, FileInfo destination)
    {
        if (!source.Exists) return;
        if (!destination.Exists)
        {
            destination.CreateAsSymbolicLink(source.FullName);
            Log.Verbose($"Making symbolic link at {destination.FullName} to {source.FullName}");
        }
        else if (destination.ResolveLinkTarget(false) is null)
        {
            destination.Delete();
            destination.CreateAsSymbolicLink(source.FullName);
            Log.Verbose($"Replacing file at {destination.FullName} with symbolic link to {source.FullName}");
        }
        else if (destination.ResolveLinkTarget(true).FullName != source.FullName)
        {
            destination.Delete();
            destination.CreateAsSymbolicLink(source.FullName);
            Log.Verbose($"Symbolic link at {destination.FullName} incorrectly links to {destination.ResolveLinkTarget(true).FullName}. Replacing with link to {source.FullName}");
        }
        else
        {
            Log.Verbose($"Symbolic link at {destination.FullName} to {source.FullName} is correct.");
        }
    }

    private static string NvidiaWineDLLPath(DirectoryInfo storage)
    {
        string nvngxPath = "";
        string PATH = Environment.GetEnvironmentVariable("XL_NVNGXPATH");

        var targets = new List<string>
        {
            Path.Combine(storage.FullName, "compatibilitytool"),
            Path.Combine(storage.FullName, "nvidia"),
            Path.Combine("/", "app", "lib"),
            Path.Combine("/", "usr", "lib", "extensions"),
            Path.Combine("/", "usr", "lib", "x86_64-linux-gnu"),
            Path.Combine("/", "usr", "lib64"),
            Path.Combine("/", "usr", "lib"),
            Path.Combine("/", "run", "host", "lib", "x86_64-linux-gnu"),
            Path.Combine("/", "run", "host", "lib64"),
            Path.Combine("/", "run", "host", "lib"),
        };

        if (!string.IsNullOrEmpty(PATH))
        {
            var firstcheck = new DirectoryInfo(PATH);
            Log.Verbose("XL_NVNGXPATH: " + firstcheck.FullName);
            targets.Insert(0, firstcheck.FullName);
        }

        var options = new EnumerationOptions();
        options.RecurseSubdirectories = true;
        options.MaxRecursionDepth = 10;

        foreach (var target in targets)
        {
            if (!Directory.Exists(target))
            {
                Log.Verbose($"DLSS: {target} directory does not exist");
                continue;
            }
            Log.Verbose($"DLSS: {target} directory exists... Searching...");

            var found = Directory.GetFiles(target, "nvngx.dll", options);
            if (found.Length > 0)
            {
                if (File.Exists(found[0]))
                    nvngxPath = new FileInfo(found[0]).DirectoryName;
                break;
            }
            Log.Verbose($"DLSS: No nvngx.dll found at {target}");
        }
        return nvngxPath;
    }

    private static async Task DownloadNvapi(HttpClient httpClient, DirectoryInfo installDirectory, string url, string checksum)
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
