using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Linq;
using System.Collections.Generic;

using XIVLauncher.Common.Unix.Compatibility.Wine.Releases;

namespace XIVLauncher.Common.Unix.Compatibility.Wine;

public class WineSettings
{
    // Native-first overrides for the d3d stack. d3d12/d3d12core must be forced
    // to native as well: vkd3d-proton ships as a small d3d12.dll forwarder plus
    // the real implementation in d3d12core.dll, and without an override Wine
    // prefers its own builtin d3d12/d3d12core, which cannot create a D3D12
    // device on DXVK's DXGI adapter (E_NOINTERFACE).
    private const string WINEDLLOVERRIDES = "msquic=,mscoree=n,b;d3d9,d3d11,d3d10core,dxgi,d3d12,d3d12core=";

    public IWineRelease WineRelease { get; private set; }
    public IToolRelease UmuLauncher { get; private set; }

    public bool FsyncOn { get; }
    public bool NTSyncOn { get; }
    public bool WaylandOn { get; }
    public string WineDLLOverrides { get; private set; }
    public string DebugVars { get; }
    public FileInfo LogFile { get; }
    public DirectoryInfo Prefix { get; }
    public XLCorePaths Paths { get; }

    public bool IsProton => WineRelease.IsProton;
    private string parentPath { get; }
    public string WinePath { get; private set; }
    public string WineServerPath { get; private set; }

    public Dictionary<string, string> EnvVars { get; private set; }

    public WineSettings(IWineRelease wineRelease, IToolRelease umuLauncher, string dlloverrides, XLCorePaths paths, string debugVars, FileInfo logFile, RBWineSyncType wineSync, bool waylandOn)
    {
        this.WineRelease = wineRelease;
        if (wineRelease.IsProton)
        {
            // Proton is always launched through the umu launcher, never by
            // calling the proton script directly.
            if (umuLauncher is null)
                throw new ArgumentNullException(nameof(umuLauncher), "The umu launcher is required for Proton.");

            this.parentPath = Path.Combine(wineRelease.ParentFolder, wineRelease.Name);
            this.WinePath = Path.Combine(parentPath, "proton");
            this.WineServerPath = Path.Combine(parentPath, "files", "bin", "wineserver");
            this.UmuLauncher = umuLauncher;
        }
        else
        {
            throw new PlatformNotSupportedException("Only Proton is supported on this platform.");
        }
        this.FsyncOn = wineSync == RBWineSyncType.FSync || wineSync == RBWineSyncType.NTSync;
        this.NTSyncOn = wineSync == RBWineSyncType.NTSync;
        this.WaylandOn = waylandOn;
        this.DebugVars = debugVars;
        this.LogFile = logFile;
        this.Prefix = paths.Prefix;
        this.Paths = paths;
        this.WineDLLOverrides = (WineSettings.WineDLLOverrideIsValid(dlloverrides) && !string.IsNullOrEmpty(dlloverrides) ? dlloverrides + ";" : "") + WINEDLLOVERRIDES;
        this.EnvVars = new Dictionary<string, string>();
        if (IsProton)
        {
            // Env vars for the umu launcher. umu-run builds a Steam-like
            // environment from these (STEAM_COMPAT_DATA_PATH etc. are derived
            // from WINEPREFIX), which is what makes Proton's own DXVK and
            // NVAPI behave as if the game were launched through Steam.
            EnvVars.Add("WINEPREFIX", Prefix.FullName);
            EnvVars.Add("PROTONPATH", parentPath);
            EnvVars.Add("STORE", "none");
            EnvVars.Add("PROTON_VERB", "runinprefix");
            EnvVars.Add("PROTON_NO_NTSYNC", NTSyncOn ? "0" : "1");
            EnvVars.Add("PROTON_USE_NTSYNC", NTSyncOn ? "1" : "0");

            if (!FsyncOn)
                EnvVars.Add("PROTON_NO_FSYNC", "1");

            if (WaylandOn)
                EnvVars.Add("PROTON_ENABLE_WAYLAND", "1");

            setSteamCompatMounts();
        }
    }

    private void setSteamCompatMounts()
    {
        var importantPaths = new System.Text.StringBuilder($"{Paths.GameFolder.FullName}:{Paths.ConfigFolder.FullName}");
        var steamCompatMounts = System.Environment.GetEnvironmentVariable("STEAM_COMPAT_MOUNTS");
        if (!string.IsNullOrEmpty(steamCompatMounts))
            importantPaths.Append(":" + steamCompatMounts.Trim(':'));

        var runtimeDir = System.Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrEmpty(runtimeDir))
        {
            for (int i = 0; i < 10; i++)
                importantPaths.Append($":{runtimeDir}/discord-ipc-{i}");
            importantPaths.Append($"{runtimeDir}/app/com.discordapp.Discord:{runtimeDir}/snap.discord-canary");
        }
        EnvVars.Add("STEAM_COMPAT_MOUNTS", importantPaths.ToString());
    }

    public static bool WineDLLOverrideIsValid(string dlls)
    {
        string[] invalid = { "msquic", "mscoree", "d3d9", "d3d11", "d3d10core", "dxgi", "d3d12", "d3d12core" };
        var format = @"^(?:(?:[a-zA-Z0-9_\-\.]+,?)+=(?:n,b|b,n|n|b|d|,|);?)+$";

        if (string.IsNullOrEmpty(dlls)) return true;
        if (invalid.Any(s => dlls.Contains(s))) return false;
        if (Regex.IsMatch(dlls, format)) return true;

        return false;
    }

    public static bool IsValidProtonBinaryPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return false;
        // Proton binary is called "proton" (no extension), lives in the root of the Proton directory
        if (File.Exists(Path.Combine(path, "proton")))
            return true;
        return false;
    }
}
