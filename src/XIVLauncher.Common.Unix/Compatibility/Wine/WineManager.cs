using System;
using System.IO;

using XIVLauncher.Common.Unix.Compatibility.Wine.Releases;

namespace XIVLauncher.Common.Unix.Compatibility.Wine;

public enum RBWineStartupType
{
    // Legacy value kept only so old configs keep loading. A saved "Managed"
    // value is treated as Proton.
    Managed,
    Proton,
    Custom,
}

public enum RBUmuLauncherType
{
    System,
    Builtin,

    /// <summary>
    /// Legacy value kept only so old configs keep loading. The umu launcher is
    /// mandatory for Proton and a saved "Disabled" value is treated as System.
    /// </summary>
    Disabled,
}

public enum RBWineSyncType
{
    ESync,
    FSync,
    NTSync,
}

/// <summary>
/// Manages the single built-in Proton release (GE-Proton) and the umu launcher.
/// There is no version list anymore: the launcher offers exactly two choices,
/// the built-in GE-Proton or a user-supplied custom Proton path (the latter is
/// handled in Program.CreateCompatToolsInstance).
/// </summary>
public class WineManager
{
    /// <summary>
    /// The one and only built-in Proton candidate (GE-Proton). It is only
    /// downloaded on demand, when the launcher starts the game with it.
    /// </summary>
    public IWineRelease BuiltinProton { get; }

    public IToolRelease Runtime { get; private set; }

    public DirectoryInfo SteamFolder { get; }

    private const string UMULAUNCHER_URL = "https://github.com/Open-Wine-Components/umu-launcher/releases/download/1.4.4/umu-launcher-1.4.4-zipapp.tar";

    private string umuFolder { get; }

    private string umuLauncherUrl { get; set; }

    public WineManager(string root)
    {
        // Locate the Steam installation. The built-in Proton is installed into
        // Steam's compatibilitytools.d so it also shows up inside Steam itself.
        var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        var compatFolder = Path.Combine(home, ".steam", "steam", "compatibilitytools.d");
        if (Directory.Exists(Path.Combine(home, ".steam", "steam", "steamapps", "common")))
        {
            this.SteamFolder = new DirectoryInfo(Path.Combine(home, ".steam", "steam"));
        }
        else
        {
            compatFolder = Path.Combine(home, ".local", "share", "Steam", "compatibilitytools.d");
            this.SteamFolder = new DirectoryInfo(Path.Combine(home, ".local", "share", "Steam"));
        }
        if (!Directory.Exists(compatFolder))
            Directory.CreateDirectory(compatFolder);

        // Umu Launcher
        this.umuFolder = Path.Combine(root, "compatibilitytool", "umu");
        this.umuLauncherUrl = UMULAUNCHER_URL;

        // The single built-in candidate: GE-Proton.
        this.BuiltinProton = new ProtonLatestRelease(compatFolder);
    }

    public void SetUmuLauncher(bool useBuiltinUmu)
    {
        var umuPath = findUmuLauncher(useBuiltinUmu);
        Runtime = umuPath is null
            ? new UmuLauncherRelease(Path.Combine(umuFolder, "umu-run"), this.umuLauncherUrl)
            : new UmuLauncherRelease(umuPath, "");
    }

    private string? findUmuLauncher(bool useBuiltinUmu)
    {
        if (useBuiltinUmu)
            return null;
        var pathArray = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':');
        foreach (string test in pathArray)
        {
            if (string.IsNullOrEmpty(test.Trim()))
                continue;
            string umu = Path.Combine(test.Trim(), "umu-run");
            if (File.Exists(umu))
                return Path.GetFullPath(umu);
        }
        return null;
    }
}
