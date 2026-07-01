using FfxivArgLauncher;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Serilog;
using XIVLauncher.Common.Util;

#if FLATPAK
#warning THIS IS A FLATPAK BUILD!!!
#endif

namespace XIVLauncher.Common.Unix.Compatibility;

public class CompatibilityTools
{
    // ---- Legacy fields (Managed Wine mode) ----
    private DirectoryInfo toolDirectory;
    private DirectoryInfo dxvkDirectory;
    private DirectoryInfo dxmtDirectory;

    private StreamWriter logWriter;

#if WINE_XIV_ARCH_LINUX
    private const string WINE_XIV_RELEASE_URL = ServerAddress.S3Address + "/xlcore/deps/wine/arch/wine-xiv-staging-fsync-git-arch-10.8.r0.g47f77594.tar.xz";
    private const string WINE_XIV_RELEASE_NAME = "wine-xiv-staging-fsync-git-10.8.r0.g47f77594";
#elif WINE_XIV_FEDORA_LINUX
    private const string WINE_XIV_RELEASE_URL = ServerAddress.S3Address + "/xlcore/deps/wine/fedora/wine-xiv-staging-fsync-git-fedora-10.8.r0.g47f77594.tar.xz";
    private const string WINE_XIV_RELEASE_NAME = "wine-xiv-staging-fsync-git-10.8.r0.g47f77594";
#elif WINE_XIV_MACOS
    private const string WINE_XIV_RELEASE_URL = ServerAddress.S3Address + "/xlcore/deps/wine/osx/xom-5.3.1/wine.tar.gz";
    private const string WINE_XIV_RELEASE_NAME = "wine";
#else
    private const string WINE_XIV_RELEASE_URL = ServerAddress.S3Address + "/xlcore/deps/wine/ubuntu/wine-xiv-staging-fsync-git-ubuntu-10.8.r0.g47f77594.tar.xz";
    private const string WINE_XIV_RELEASE_NAME = "wine-xiv-staging-fsync-git-10.8.r0.g47f77594";
#endif

    private const string SD_WINE_XIV_RELEASE_URL = ServerAddress.S3Address + "/xlcore/deps/wine/ubuntu/wine-xiv-staging-fsync-git-ubuntu-10.8.r0.g47f77594.tar.xz";
    private const string SD_WINE_XIV_RELEASE_NAME = "wine-xiv-staging-fsync-git-10.8.r0.g47f77594";

    public bool IsToolReady { get; private set; }

    public WineSettings Settings { get; private set; }
    public static bool IsSteamDeckHardware => Directory.Exists("/home/deck");

    /// <summary>
    /// Gets the prefix directory for both legacy and Proton modes.
    /// In Proton mode, Settings is null — use this property instead.
    /// </summary>
    public DirectoryInfo Prefix => _useProtonMode
        ? _protonSettings.Prefix
        : (Settings?.Prefix ?? throw new InvalidOperationException("CompatibilityTools not initialized"));

    // ---- New Proton/UMU fields ----
    private bool _useProtonMode;
    private Wine.WineSettings _protonSettings;
    private IToolRelease _dxvkVersion;
    private IToolRelease _nvapiVersion;
    private RBHudType _rbHudType;
    private string _customHud;
    private bool _dxvkAsyncOn;
    private bool _gplAsyncCacheOn;
    private int _dxvkFrameRate;
    private List<ExtraCommand> _extraCommands;
    private DirectoryInfo _storageDirectory;
    private DirectoryInfo _wineDirectory;
    private DirectoryInfo _umuDirectory;
    private DirectoryInfo _nvapiDirectory;
    private DirectoryInfo _gameDirectory;
    private DirectoryInfo _configDirectory;
    private DirectoryInfo _steamDirectory;

    private bool _isDxvkEnabled => _useProtonMode || _dxvkVersion?.Label != "Disabled";
    private bool _isNvapiEnabled => (_useProtonMode || _isDxvkEnabled) && (_nvapiVersion?.Label != "Disabled");

    private string Wine64Path => _useProtonMode ? _protonSettings.WinePath :
        (Settings.StartupType == WineStartupType.Managed
            ? Path.Combine(toolDirectory.FullName, IsSteamDeckHardware ? SD_WINE_XIV_RELEASE_NAME : WINE_XIV_RELEASE_NAME, "bin")
            : Settings.CustomBinPath);

    private string WineLibPath => _useProtonMode ? Path.Combine(Path.GetDirectoryName(_protonSettings.WinePath), "..", "lib") :
        (Settings.StartupType == WineStartupType.Managed
            ? Path.Combine(toolDirectory.FullName, WINE_XIV_RELEASE_NAME, "lib")
            : Path.Combine(Settings.CustomBinPath, "..", "lib"));

    // private string MoltenVkPath => Path.Combine(Paths.ResourcesPath, "MoltenVK");
    private string Wine64PathFull => _useProtonMode ? _protonSettings.WinePath :
        Path.Combine(Wine64Path, "wine64");
    private string WineServerPathFull => _useProtonMode ? _protonSettings.WineServerPath :
        Path.Combine(Wine64Path, "wineserver");

    private string RuntimePath => _useProtonMode
        ? (_protonSettings.IsUsingUmu ? _protonSettings.UmuLauncher.Name : Wine64PathFull)
        : Wine64PathFull;

    public bool IsToolDownloaded => _useProtonMode
        ? (File.Exists(RuntimePath) && File.Exists(Wine64PathFull) && (_protonSettings?.Prefix.Exists ?? false))
        : (File.Exists(Wine64PathFull) && Settings.Prefix.Exists);

    private readonly DxvkHudType hudType;
    private readonly bool gamemodeOn;
    private readonly string dxvkAsyncOn;
    private readonly int dxvkFrameLimit;
    private readonly bool dxmtEnabled;
    private readonly bool metalFxEnabled;
    private readonly int metalFxFactor;

    // ---- Legacy Constructor (UNCHANGED) ----
    public CompatibilityTools(
        WineSettings wineSettings,
        DxvkHudType hudType,
        bool? gamemodeOn,
        bool? dxvkAsyncOn,
        int dxvkFrameLimit,
        DirectoryInfo toolsFolder,
        bool? dxmtEnabled,
        bool? metalFxEnabled,
        int? metalFxFactor)
    {
        this._useProtonMode = false;
        this.Settings = wineSettings;
        this.hudType = hudType;
        this.gamemodeOn = gamemodeOn ?? false;
        this.dxvkAsyncOn = (dxvkAsyncOn ?? false) ? "1" : "0";
        this.dxvkFrameLimit = dxvkFrameLimit;
        this.dxmtEnabled = dxmtEnabled ?? false;
        this.metalFxEnabled = metalFxEnabled ?? false;
        this.metalFxFactor = metalFxFactor ?? 2;

        this.toolDirectory = new DirectoryInfo(Path.Combine(toolsFolder.FullName, "beta"));
        this.dxvkDirectory = new DirectoryInfo(Path.Combine(toolsFolder.FullName, "dxvk"));
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            this.dxmtDirectory = new DirectoryInfo(Path.Combine(toolsFolder.FullName, "dxmt"));
        }

        this.logWriter = new StreamWriter(wineSettings.LogFile.FullName);

        if (wineSettings.StartupType == WineStartupType.Managed)
        {
            if (!this.toolDirectory.Exists)
                this.toolDirectory.Create();

            if (!this.dxvkDirectory.Exists)
                this.dxvkDirectory.Create();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                if (!this.dxmtDirectory!.Exists)
                    this.dxmtDirectory.Create();
            }
        }

        if (!wineSettings.Prefix.Exists)
            wineSettings.Prefix.Create();
    }

    // ---- New Proton/UMU Constructor ----
    public CompatibilityTools(
        Wine.WineSettings wineSettings,
        IToolRelease dxvkVersion,
        int frameRate,
        RBHudType hudType,
        string customHud,
        IToolRelease nvapiVersion,
        bool gamemodeOn,
        bool dxvkAsyncOn,
        bool gplAsyncCacheOn,
        List<ExtraCommand> commands)
    {
        this._useProtonMode = true;
        this._protonSettings = wineSettings;
        this._dxvkVersion = dxvkVersion;
        this._dxvkFrameRate = (frameRate == 0 || frameRate >= 30) ? frameRate : 0;
        this._rbHudType = hudType;
        this._customHud = customHud;
        this._nvapiVersion = _dxvkVersion.Name != "DISABLED" ? nvapiVersion : new Nvapi.Releases.NvapiCustomRelease("Disabled", "Do not use Nvapi", "DISABLED", "");
        this.gamemodeOn = gamemodeOn;
        this._dxvkAsyncOn = dxvkAsyncOn;
        this._gplAsyncCacheOn = gplAsyncCacheOn;
        this._extraCommands = commands;
        this._storageDirectory = wineSettings.Paths.StorageFolder;
        this._wineDirectory = new DirectoryInfo(Path.Combine(wineSettings.Paths.StorageFolder.FullName, "compatibilitytool", "wine"));
        this.dxvkDirectory = new DirectoryInfo(Path.Combine(wineSettings.Paths.StorageFolder.FullName, "compatibilitytool", "dxvk"));
        this._nvapiDirectory = new DirectoryInfo(Path.Combine(wineSettings.Paths.StorageFolder.FullName, "compatibilitytool", "nvapi"));
        this._umuDirectory = new DirectoryInfo(Path.Combine(wineSettings.Paths.StorageFolder.FullName, "compatibilitytool", "umu"));
        this._gameDirectory = wineSettings.Paths.GameFolder;
        this._configDirectory = wineSettings.Paths.ConfigFolder;
        this._steamDirectory = wineSettings.Paths.SteamFolder;

        this._wineDirectory.Create();
        this.dxvkDirectory.Create();
        this._nvapiDirectory.Create();
        this._umuDirectory.Create();

        this.logWriter = new StreamWriter(wineSettings.LogFile.FullName);

        if (!this._steamDirectory.Exists && this._protonSettings.IsUsingUmu)
        {
            this._steamDirectory.Create();
            this._steamDirectory.CreateSubdirectory(Path.Combine("compatibilitytools.d"));
        }

        var pfx = new FileInfo(Path.Combine(wineSettings.Prefix.FullName, "pfx"));

        if (!wineSettings.Prefix.Exists)
            wineSettings.Prefix.Create();

        // Do proton prefixes like umu, with pfx symlinked back to prefix folder.
        if (wineSettings.IsProton)
        {
            if (pfx.Exists)
            {
                if (pfx.ResolveLinkTarget(false) is null)
                {
                    pfx.Delete();
                    pfx.CreateAsSymbolicLink(wineSettings.Prefix.FullName);
                }
                if (pfx.ResolveLinkTarget(true).FullName != wineSettings.Prefix.FullName)
                {
                    pfx.Delete();
                    pfx.CreateAsSymbolicLink(wineSettings.Prefix.FullName);
                }
            }
            else if (!Directory.Exists(pfx.FullName))
            {
                pfx.CreateAsSymbolicLink(wineSettings.Prefix.FullName);
            }
        }
    }

    // ---- EnsureTool ----
    public async Task EnsureTool(DirectoryInfo tempPath)
    {
        if (_useProtonMode)
        {
            await EnsureToolProton(tempPath).ConfigureAwait(false);
            return;
        }

        // Legacy Managed Wine mode (UNCHANGED)
        if (!File.Exists(Wine64PathFull))
        {
            Log.Information("Compatibility tool does not exist, downloading");
            await DownloadTool(tempPath).ConfigureAwait(false);
            Log.Information("Download compatibility tool finished.");
        }

        EnsurePrefix();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            if (this.dxmtEnabled)
            {
                await Dxmt.InstallDxmt(Settings.Prefix, dxmtDirectory).ConfigureAwait(false);
            }
            else
            {
                await Dxmt.UninstallDxmt(Settings.Prefix, dxmtDirectory).ConfigureAwait(false);
                using (var httpClient = new HttpClient())
                {
                    httpClient.DefaultRequestHeaders.Add("User-Agent", PlatformHelpers.GetVersion());
                    await Dxvk.Dxvk.InstallDxvk(httpClient, Settings.Prefix, dxvkDirectory, new Dxvk.Releases.DxvkStableAsyncRelease()).ConfigureAwait(false);
                }
            }
        }
        else
        {
            using (var httpClient = new HttpClient())
            {
                httpClient.DefaultRequestHeaders.Add("User-Agent", PlatformHelpers.GetVersion());
                await Dxvk.Dxvk.InstallDxvk(httpClient, Settings.Prefix, dxvkDirectory, new Dxvk.Releases.DxvkStableAsyncRelease()).ConfigureAwait(false);
            }
        }

        IsToolReady = true;
    }

    public async Task EnsureTool(HttpClient httpClient, DirectoryInfo tempPath)
    {
        if (!_useProtonMode)
        {
            await EnsureTool(tempPath).ConfigureAwait(false);
            return;
        }

        // Proton/UMU tool setup
        if (_protonSettings.IsUsingUmu)
        {
            var downloadUrlExists = !string.IsNullOrEmpty(_protonSettings.UmuLauncher.DownloadUrl);
            if (!File.Exists(RuntimePath) && !downloadUrlExists)
                throw new ArgumentNullException("Umu Launcher selected, but is not present, and no download url provided.");

            if (downloadUrlExists)
            {
                var downloadUmu = false;
                var urlParts = _protonSettings.UmuLauncher.DownloadUrl.Split('/');
                var webVersion = urlParts[urlParts.Length - 2];
                if (File.Exists(Path.Combine(_umuDirectory.FullName, "version")))
                {
                    var currentVersion = File.ReadAllText(Path.Combine(_umuDirectory.FullName, "version")).Trim();
                    if (currentVersion != webVersion)
                    {
                        downloadUmu = true;
                        Log.Information($"[UMU] Umu Launcher version mismatch. Current version: {currentVersion}, expected version: {webVersion}. Downloading...");
                    }
                }
                else
                {
                    downloadUmu = true;
                    Log.Information($"[UMU] Umu Launcher version file not found. Expected at {Path.Combine(_umuDirectory.FullName, "version")}. Downloading...");
                }

                if (downloadUmu)
                {
                    if (string.IsNullOrEmpty(_protonSettings.UmuLauncher.DownloadUrl))
                        throw new ArgumentNullException("Umu Launcher selected, but is not present, and no download url provided.");
                    _umuDirectory.Delete(true);
                    _umuDirectory.Create();
                    Log.Information($"[UMU] umu-run is not in $PATH, downloading {_protonSettings.UmuLauncher.DownloadUrl} to {_umuDirectory.FullName}");
                    await Wine.Runtime.DownloadRuntime(httpClient, _umuDirectory.Parent, _protonSettings.UmuLauncher.DownloadUrl).ConfigureAwait(false);
                    File.WriteAllText(Path.Combine(_umuDirectory.FullName, "version"), webVersion);
                }
            }
        }

        if (_protonSettings.IsProton)
        {
            if (!File.Exists(Wine64PathFull))
            {
                if (string.IsNullOrEmpty(_protonSettings.WineRelease.DownloadUrl))
                    throw new ArgumentNullException($"Proton not found at {Wine64PathFull}, and no download url provided.");
                Log.Information($"{_protonSettings.WineRelease.Label} does not exist. Downloading {_protonSettings.WineRelease.DownloadUrl} to {_protonSettings.WineRelease.ParentFolder}");
                await DownloadTool(httpClient, new DirectoryInfo(_protonSettings.WineRelease.ParentFolder), tempPath).ConfigureAwait(false);
            }
            EnsurePrefix();

            // Install selected DXVK version over Proton's built-in. When DXVK version
            // is set to "Disabled", Proton's built-in DXVK is used as-is.
            if (_isDxvkEnabled && _dxvkVersion?.Name != "DISABLED")
            {
                await Dxvk.Dxvk.InstallDxvk(httpClient, _protonSettings.Prefix, dxvkDirectory, _dxvkVersion).ConfigureAwait(false);
            }

            // Install dxvk-nvapi for DLSS support. We download the version-matched
            // dxvk-nvapi ourselves rather than relying on Proton's PROTON_ENABLE_NVAPI=1
            // (which may not trigger correctly outside Steam). Symlink nvngx.dll from
            // the NVIDIA driver for the actual DLSS runtime.
            if (_isNvapiEnabled)
            {
                await Nvapi.Nvapi.InstallNvapi(httpClient, _protonSettings.Prefix, _nvapiDirectory, _nvapiVersion).ConfigureAwait(false);
                Nvapi.Nvapi.CopyNvngx(_protonSettings.Paths.GameFolder, _protonSettings.Prefix, _storageDirectory);
            }

            IsToolReady = true;
            return;
        }

        if (!File.Exists(Wine64PathFull))
        {
            if (string.IsNullOrEmpty(_protonSettings.WineRelease.DownloadUrl))
                throw new ArgumentNullException($"Wine not found at the given path: {Wine64PathFull}, and no download url provided.");
            Log.Information($"Wine release \"{_protonSettings.WineRelease.Label}\" does not exist. Downloading {_protonSettings.WineRelease.DownloadUrl} to {_wineDirectory.FullName}");
            await DownloadTool(httpClient, _wineDirectory, tempPath).ConfigureAwait(false);
            _protonSettings.SetWineOrWine64(new FileInfo(Wine64PathFull).Directory.FullName);
        }

        EnsurePrefix();

        if (_isDxvkEnabled)
            await Dxvk.Dxvk.InstallDxvk(httpClient, _protonSettings.Prefix, dxvkDirectory, _dxvkVersion).ConfigureAwait(false);
        if (_isNvapiEnabled)
        {
            await Nvapi.Nvapi.InstallNvapi(httpClient, _protonSettings.Prefix, _nvapiDirectory, _nvapiVersion).ConfigureAwait(false);
            Nvapi.Nvapi.CopyNvngx(_protonSettings.Paths.GameFolder, _protonSettings.Prefix, _storageDirectory);
        }
        IsToolReady = true;
    }

    private async Task EnsureToolProton(DirectoryInfo tempPath)
    {
        // This path is kept for backwards compatibility callers that don't pass HttpClient
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("User-Agent", PlatformHelpers.GetVersion());
        await EnsureTool(client, tempPath).ConfigureAwait(false);
    }

    // ---- Download ----
    private async Task DownloadTool(DirectoryInfo tempPath)
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("User-Agent", PlatformHelpers.GetVersion());
        var tempFilePath = Path.Combine(tempPath.FullName, $"{Guid.NewGuid()}");

        var wineUrl = IsSteamDeckHardware ? SD_WINE_XIV_RELEASE_URL : WINE_XIV_RELEASE_URL;

        var fileBytes = await client
            .GetByteArrayAsync(wineUrl)
            .ConfigureAwait(false);

        Log.Information("Downloaded wine from {Path}", wineUrl);

        await File.WriteAllBytesAsync(tempFilePath, fileBytes).ConfigureAwait(false);

        Log.Information("Wine saved to {Path}", tempFilePath);

        PlatformHelpers.Untar(tempFilePath, this.toolDirectory.FullName);

        Log.Information("Wine unzipped to {Path}", tempFilePath);

        Log.Information("Compatibility tool successfully extracted to {Path}", this.toolDirectory.FullName);

        File.Delete(tempFilePath);
    }

    private async Task DownloadTool(HttpClient httpClient, DirectoryInfo targetPath, DirectoryInfo tempPath)
    {
        var tempFilePath = Path.Combine(tempPath.FullName, $"{Guid.NewGuid()}");
        await File.WriteAllBytesAsync(tempFilePath, await httpClient.GetByteArrayAsync(_protonSettings.WineRelease.DownloadUrl).ConfigureAwait(false)).ConfigureAwait(false);
        if (!Wine.CompatUtil.EnsureChecksumMatch(tempFilePath, _protonSettings.WineRelease.Checksums))
        {
            throw new InvalidDataException("SHA512 checksum verification failed");
        }
        PlatformHelpers.Untar(tempFilePath, targetPath.FullName);
        Log.Information("Compatibility tool {Name} successfully extracted to {Path}", _protonSettings.WineRelease.Label, targetPath.FullName);
        File.Delete(tempFilePath);
    }

    // ---- Prefix management ----
    private void ResetPrefix()
    {
        if (_useProtonMode)
        {
            _protonSettings.Prefix.Refresh();
            if (_protonSettings.Prefix.Exists)
                _protonSettings.Prefix.Delete(true);
            _protonSettings.Prefix.Create();
            EnsurePrefix();
            return;
        }

        Settings.Prefix.Refresh();

        if (Settings.Prefix.Exists)
            Settings.Prefix.Delete(true);

        Settings.Prefix.Create();
        EnsurePrefix();
    }

    public void EnsurePrefix()
    {
        if (_useProtonMode)
        {
            // Delete lsteamclient.dll to prevent crashes
            var lsteamclient = new FileInfo(Path.Combine(_protonSettings.Prefix.FullName, "drive_c", "windows", "system32", "lsteamclient.dll"));
            if (lsteamclient.Exists)
            {
                lsteamclient.Delete();
                Log.Verbose("Using custom wine or non-lsteamclient wine. Deleting lsteamclient.dll from prefix.");
            }
            var verb = "runinprefix";
            if (!File.Exists(Path.Combine(_protonSettings.Prefix.FullName, "config_info")) &&
                !File.Exists(Path.Combine(_protonSettings.Prefix.FullName, "pfx.lock")) &&
                !File.Exists(Path.Combine(_protonSettings.Prefix.FullName, "tracked_files")) &&
                !File.Exists(Path.Combine(_protonSettings.Prefix.FullName, "version")))
            {
                verb = "run";
            }
            RunWithoutRuntime("cmd /c dir %userprofile%/Documents > nul", verb, false).WaitForExit();
            return;
        }

        RunInPrefix("cmd /c dir %userprofile%/Documents > nul").WaitForExit();
    }

    // ---- RunInPrefix variants ----
    public Process RunWithoutRuntime(string command, string verb = "runinprefix", bool redirect = true)
    {
        if (!_useProtonMode || !_protonSettings.IsProton)
            return RunInPrefix(command, redirectOutput: redirect, writeLog: redirect);

        var psi = new ProcessStartInfo(Wine64PathFull);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        foreach (var kvp in _protonSettings.EnvVars)
            psi.Environment.Add(kvp);
        psi.Environment.Add("WINEDLLOVERRIDES", _protonSettings.WineDLLOverrides + (_isDxvkEnabled ? "n,b" : "b"));
        psi.Environment.Add("STEAM_COMPAT_DATA_PATH", _protonSettings.Prefix.FullName);
        psi.Environment.Add("STEAM_COMPAT_CLIENT_INSTALL_PATH", _protonSettings.Paths.SteamFolder.FullName);
        psi.Arguments = verb + " " + command;
        var quickRun = new Process();
        quickRun.StartInfo = psi;
        quickRun.Start();
        Log.Verbose("Running without runtime: {FileName} {Arguments}", psi.FileName, psi.Arguments);
        return quickRun;
    }

    public Process RunTheGame(string command, string workingDirectory = "", IDictionary<string, string> environment = null, bool redirectOutput = false, bool writeLog = false, bool wineD3D = false)
    {
        if (!_useProtonMode || _extraCommands is null)
            return RunInPrefix(command, workingDirectory, environment, redirectOutput, writeLog, wineD3D);

        var leadProcess = "";
        var extraArgs = "";
        var first = true;

        foreach (var extra in _extraCommands)
        {
            if (first)
            {
                leadProcess = extra.Command;
                first = false;
            }
            else
                extraArgs += extra.Command + " ";
            extraArgs += extra.Arguments + " ";
            Log.Information($"Using extra command {extra.Command} with arguments \"{extra.Arguments}\"");
        }

        var psi = new ProcessStartInfo(leadProcess);
        extraArgs += RuntimePath + " ";
        if (!_protonSettings.IsUsingUmu && _protonSettings.IsProton)
            psi.Arguments = extraArgs + "runinprefix " + command;
        else
            psi.Arguments = extraArgs + command;
        return RunInPrefix(psi, workingDirectory, environment, redirectOutput, writeLog, wineD3D);
    }

    public Process RunInPrefix(string command, string workingDirectory = "", IDictionary<string, string> environment = null, bool redirectOutput = false, bool writeLog = false, bool wineD3D = false)
    {
        if (_useProtonMode)
        {
            var psi = new ProcessStartInfo(RuntimePath);
            if (!_protonSettings.IsUsingUmu && _protonSettings.IsProton)
                psi.Arguments = "runinprefix " + command;
            else
                psi.Arguments = command;
            Log.Verbose("Running in prefix: {FileName} {Arguments}", psi.FileName, psi.Arguments);
            return RunInPrefix(psi, workingDirectory, environment, redirectOutput, writeLog, wineD3D);
        }

        // Legacy mode (UNCHANGED)
        var legacyPsi = new ProcessStartInfo(Wine64PathFull);
        legacyPsi.Arguments = command;

        Log.Verbose("Running in prefix: {FileName} {Arguments}", legacyPsi.FileName, command);
        return RunInPrefix(legacyPsi, workingDirectory, environment, redirectOutput, writeLog, wineD3D);
    }

    public Process RunInPrefix(string[] args, string workingDirectory = "", IDictionary<string, string> environment = null, bool redirectOutput = false, bool writeLog = false, bool wineD3D = false)
    {
        if (_useProtonMode)
        {
            var psi = new ProcessStartInfo(RuntimePath);
            if (!_protonSettings.IsUsingUmu && _protonSettings.IsProton)
                psi.ArgumentList.Add("runinprefix");
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            Log.Verbose("Running in prefix: {FileName} {Arguments}", psi.FileName, psi.ArgumentList.Aggregate(string.Empty, (a, b) => a + " " + b));
            return RunInPrefix(psi, workingDirectory, environment, redirectOutput, writeLog, wineD3D);
        }

        var legacyPsi = new ProcessStartInfo(Wine64PathFull);
        foreach (var arg in args)
            legacyPsi.ArgumentList.Add(arg);

        Log.Verbose("Running in prefix: {FileName} {Arguments}", legacyPsi.FileName, legacyPsi.ArgumentList.Aggregate(string.Empty, (a, b) => a + " " + b));
        return RunInPrefix(legacyPsi, workingDirectory, environment, redirectOutput, writeLog, wineD3D);
    }

    private void MergeDictionaries(StringDictionary a, IDictionary<string, string> b)
    {
        if (b is null)
            return;

        foreach (var keyValuePair in b)
        {
            if (a.ContainsKey(keyValuePair.Key))
            {
                if (_useProtonMode && keyValuePair.Key == "LD_PRELOAD")
                    a[keyValuePair.Key] = MergeLDPreload(a[keyValuePair.Key], keyValuePair.Value);
                else
                    a[keyValuePair.Key] = keyValuePair.Value;
            }
            else
                a.Add(keyValuePair.Key, keyValuePair.Value);
        }
    }

    private string MergeLDPreload(string a, string b)
    {
        a ??= "";
        b ??= "";
        return (a.Trim(':') + ":" + b.Trim(':')).Trim(':');
    }

    private Process RunInPrefix(ProcessStartInfo psi, string workingDirectory, IDictionary<string, string> environment, bool redirectOutput, bool writeLog, bool wineD3D)
    {
        psi.RedirectStandardOutput = redirectOutput;
        psi.RedirectStandardError = writeLog;
        psi.UseShellExecute = false;
        psi.WorkingDirectory = workingDirectory;

        var wineEnviromentVariables = new Dictionary<string, string>();

        if (_useProtonMode)
        {
            var ogl = wineD3D || !_isDxvkEnabled;

            wineEnviromentVariables.Add("WINEDLLOVERRIDES", _protonSettings.WineDLLOverrides + (ogl ? "b" : "n,b"));

            if (!ogl)
            {
                wineEnviromentVariables.Add("DXVK_FRAME_RATE", _dxvkFrameRate.ToString());
                if (_isNvapiEnabled)
                    wineEnviromentVariables.Add("DXVK_ENABLE_NVAPI", "1");
                else if (_protonSettings.IsProton)
                    wineEnviromentVariables.Add("PROTON_DISABLE_NVAPI", "1");
            }

            if (!string.IsNullOrEmpty(_protonSettings.DebugVars))
                wineEnviromentVariables.Add("WINEDEBUG", _protonSettings.DebugVars);

            wineEnviromentVariables.Add("XL_WINEONLINUX", "true");

            string ldPreload = Environment.GetEnvironmentVariable("LD_PRELOAD") ?? "";

            var dxvkHud = _rbHudType switch
            {
                RBHudType.None => "",
                RBHudType.Fps => "fps",
                RBHudType.Full => "full",
                _ => "",
            };

            if (!string.IsNullOrEmpty(dxvkHud))
                wineEnviromentVariables.Add("DXVK_HUD", dxvkHud);

            if (this.gamemodeOn && !ldPreload.Contains("libgamemodeauto.so.0"))
            {
                ldPreload = ldPreload == "" ? "libgamemodeauto.so.0" : ldPreload + ":libgamemodeauto.so.0";
            }

            if (_dxvkAsyncOn)
            {
                wineEnviromentVariables.Add("DXVK_ASYNC", "1");
                wineEnviromentVariables.Add("DXVK_GPLASYNCCACHE", _gplAsyncCacheOn ? "1" : "0");
            }

            wineEnviromentVariables.Add("LD_PRELOAD", ldPreload);

            MergeDictionaries(psi.EnvironmentVariables, wineEnviromentVariables);
            MergeDictionaries(psi.EnvironmentVariables, _protonSettings.EnvVars);
            MergeDictionaries(psi.EnvironmentVariables, environment);
        }
        else
        {
            // ---- Legacy env vars (UNCHANGED) ----
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                wineEnviromentVariables.Add("LANG", "en_US");
                wineEnviromentVariables.Add("MVK_ALLOW_METAL_FENCES", "1");
                wineEnviromentVariables.Add("MVK_CONFIG_FULL_IMAGE_VIEW_SWIZZLE", "1");
                wineEnviromentVariables.Add("MVK_CONFIG_RESUME_LOST_DEVICE", "1");
                wineEnviromentVariables.Add("DOTNET_EnableWriteXorExecute", "0");
                wineEnviromentVariables.Add("DXMT_CONFIG", $"d3d11.metalSpatialUpscaleFactor={this.metalFxFactor};d3d11.preferredMaxFrameRate={this.dxvkFrameLimit};");
                wineEnviromentVariables.Add("DXMT_METALFX_SPATIAL_SWAPCHAIN", this.metalFxEnabled ? "1" : "0");
            }

            wineEnviromentVariables.Add("WINEPREFIX", Settings.Prefix.FullName);
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                wineEnviromentVariables.Add("WINEDLLOVERRIDES", $"msquic=,mscoree=n,b;d3d9,d3d11,d3d10core,dxgi={(wineD3D ? "b" : "n")}");
            }
            else
            {
                wineEnviromentVariables.Add("WINEDLLOVERRIDES", $"msquic=,mscoree=n,b;d3d11={(wineD3D ? "b" : "n")};dxgi=n,b");
            }

            if (!string.IsNullOrEmpty(Settings.DebugVars))
            {
                wineEnviromentVariables.Add("WINEDEBUG", Settings.DebugVars);
            }

            if (!string.IsNullOrEmpty(Settings.Env))
            {
                var envList = Settings.Env.Split(';');

                foreach (var env in envList)
                {
                    var kvList = env.Split('=');
                    wineEnviromentVariables.Add(kvList[0], kvList[1]);
                }
            }

            wineEnviromentVariables.Add("XL_WINEONLINUX", "true");

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                wineEnviromentVariables.Add("XL_WINEONMAC", "true");
            }

            string ldPreload = Environment.GetEnvironmentVariable("LD_PRELOAD") ?? "";

            string dxvkHud = hudType switch
            {
                DxvkHudType.None => "0",
                DxvkHudType.Fps => "fps",
                DxvkHudType.Full => "full",
                _ => throw new ArgumentOutOfRangeException()
            };

            if (this.gamemodeOn && !ldPreload.Contains("libgamemodeauto.so.0"))
            {
                ldPreload = ldPreload == "" ? "libgamemodeauto.so.0" : ldPreload + ":libgamemodeauto.so.0";
            }

            wineEnviromentVariables.Add("DXVK_HUD", dxvkHud);
            wineEnviromentVariables.Add("DXVK_ASYNC", dxvkAsyncOn);
            wineEnviromentVariables.Add("DXVK_STATE_CACHE_PATH", "C:\\");
            wineEnviromentVariables.Add("DXVK_LOG_PATH", "C:\\");
            wineEnviromentVariables.Add("DXVK_CONFIG_FILE", "C:\\ffxiv_dx11.conf");
            wineEnviromentVariables.Add("WINEESYNC", Settings.EsyncOn);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                wineEnviromentVariables.Add("WINEMSYNC", Settings.MsyncOn);
            }
            else
            {
                wineEnviromentVariables.Add("WINEFSYNC", Settings.FsyncOn);
            }

            if (dxvkFrameLimit != 0)
            {
                wineEnviromentVariables.Add("DXVK_FRAME_RATE", dxvkFrameLimit.ToString());
            }

            wineEnviromentVariables.Add("LD_PRELOAD", ldPreload);

            MergeDictionaries(psi.EnvironmentVariables, wineEnviromentVariables);
            MergeDictionaries(psi.EnvironmentVariables, environment);
        }

#if DEBUG
        Log.Debug($"Running in prefix: {psi.FileName} {psi.Arguments}");
        Log.Debug("with wineEnviromentVariables:");
        foreach (string k in psi.EnvironmentVariables.Keys)
        {
            Log.Debug(k + "=" + psi.EnvironmentVariables[k]);
        }
#endif

#if FLATPAK_NOTRIGHTNOW
        psi.FileName = "flatpak-spawn";

        psi.ArgumentList.Insert(0, "--host");
        psi.ArgumentList.Insert(1, Wine64PathFull);

        foreach (KeyValuePair<string, string> envVar in wineEnviromentVariables)
        {
            psi.ArgumentList.Insert(1, $"--env={envVar.Key}={envVar.Value}");
        }

        if (environment != null)
        {
            foreach (KeyValuePair<string, string> envVar in environment)
            {
                psi.ArgumentList.Insert(1, $"--env=\"{envVar.Key}\"=\"{envVar.Value}\"");
            }
        }
#endif

        Process helperProcess = new();
        helperProcess.StartInfo = psi;
        helperProcess.ErrorDataReceived += new DataReceivedEventHandler((_, errLine) =>
        {
            if (String.IsNullOrEmpty(errLine.Data))
                return;

            try
            {
                logWriter.WriteLine(errLine.Data);
                Console.Error.WriteLine(errLine.Data);
            }
            catch (Exception ex) when (ex is ArgumentOutOfRangeException ||
                                       ex is OverflowException ||
                                       ex is IndexOutOfRangeException)
            {
            }
        });

        helperProcess.Start();
        if (writeLog)
            helperProcess.BeginErrorReadLine();

        return helperProcess;
    }

    // ---- Process management ----
    public Int32[] GetProcessIds(string executableName)
    {
        var wineDbg = RunInPrefix("winedbg --command \"info proc\"", redirectOutput: true);
        var output = wineDbg.StandardOutput.ReadToEnd();
        var matchingLines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => l.Contains(executableName));
        return matchingLines.Select(l => int.Parse(l.Substring(1, 8), System.Globalization.NumberStyles.HexNumber)).ToArray();
    }

    public Int32 GetProcessId(string executableName)
    {
        return GetProcessIds(executableName).FirstOrDefault();
    }

    private static readonly string[] KnownGameExes = { "ffxiv_dx11.exe", "ffxiv.exe", "ffxivlauncher.exe" };

    public Int32 GetUnixProcessId(Int32 winePid)
    {
        if (_useProtonMode && _protonSettings.IsProton)
        {
            // For Proton mode, try winedbg first (works with XIV Proton),
            // then fall back to direct process name lookup.
            var pid = TryGetUnixPidFromWineDbgProcmap(winePid);
            if (pid != 0)
                return pid;

            Log.Warning("winedbg info procmap failed for Proton mode, trying direct process lookup");
            return FindGameProcessByName();
        }

        // Legacy mode
        var wineDbg = RunInPrefix("winedbg --command \"info procmap\"", redirectOutput: true);
        var output = wineDbg.StandardOutput.ReadToEnd();
        if (output.Contains("syntax error\n") || output.Contains("Exception c0000005"))
        {
            var processName = GetProcessNameFromWineDbg(winePid);
            var namedPid = GetUnixProcessIdByName(processName);
            if (namedPid != 0) return namedPid;
            return FindGameProcessByName();
        }

        var matchingLines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).Where(
            l =>
            {
                if (l.Length < 18) return false;
                if (int.TryParse(l.AsSpan(1, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int pid))
                    return pid == winePid;
                return false;
            });
        var unixPids = matchingLines.Select(l => int.Parse(l.AsSpan(10, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
        return unixPids.FirstOrDefault();
    }

    /// <summary>
    /// Try to get Unix PID via winedbg info procmap. Returns 0 on failure.
    /// </summary>
    private Int32 TryGetUnixPidFromWineDbgProcmap(Int32 winePid)
    {
        try
        {
            var wineDbg = RunInPrefix("winedbg --command \"info procmap\"", redirectOutput: true);
            var output = wineDbg.StandardOutput.ReadToEnd();

            if (string.IsNullOrEmpty(output) ||
                output.Contains("syntax error\n") ||
                output.Contains("Exception c0000005"))
                return 0;

            var matchingLines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).Where(
                l =>
                {
                    if (l.Length < 18) return false;
                    if (int.TryParse(l.AsSpan(1, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int pid))
                        return pid == winePid;
                    return false;
                });
            var unixPids = matchingLines.Select(l => int.Parse(l.AsSpan(10, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
            return unixPids.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "winedbg info procmap threw an exception");
            return 0;
        }
    }

    /// <summary>
    /// Fallback: find the game process by known executable names.
    /// Picks the process started most recently (likely our game).
    /// </summary>
    private Int32 FindGameProcessByName()
    {
        var currentProcess = Process.GetCurrentProcess();
        var currentStartTime = currentProcess.StartTime;

        foreach (var exeName in KnownGameExes)
        {
            Process? bestMatch = null;

            try
            {
                foreach (var process in Process.GetProcessesByName(exeName))
                {
                    try
                    {
                        // Pick the process started AFTER the launcher (most recent)
                        if (process.StartTime > currentStartTime)
                        {
                            if (bestMatch == null || process.StartTime > bestMatch.StartTime)
                                bestMatch = process;
                        }
                    }
                    catch
                    {
                        // Skip processes we can't read start time from
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Verbose(ex, "Could not enumerate processes by name {ExeName}", exeName);
                continue;
            }

            if (bestMatch != null)
            {
                Log.Information("Game process found by name {ExeName}: pid {Pid}", exeName, bestMatch.Id);
                return bestMatch.Id;
            }
        }

        Log.Error("Could not find game process by any known executable name");
        return 0;
    }

    private string? GetProcessNameFromWineDbg(Int32 winePid)
    {
        try
        {
            var wineDbg = RunInPrefix("winedbg --command \"info proc\"", redirectOutput: true);
            var output = wineDbg.StandardOutput.ReadToEnd();
            if (string.IsNullOrEmpty(output))
                return null;

            var matchingLines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).Where(
                l =>
                {
                    if (l.Length < 20) return false;
                    if (int.TryParse(l.AsSpan(1, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int pid))
                        return pid == winePid;
                    return false;
                });
            var processNames = matchingLines.Select(l => l.Substring(20).Trim('\'')).ToArray();
            return processNames.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "winedbg info proc threw an exception");
            return null;
        }
    }

    private Int32 GetUnixProcessIdByName(string? executableName)
    {
        if (string.IsNullOrEmpty(executableName))
            return 0;

        int closest = 0;
        int early = 0;
        var currentProcess = Process.GetCurrentProcess();
        bool nonunique = false;
        foreach (var process in Process.GetProcessesByName(executableName))
        {
            if (process.Id < currentProcess.Id)
            {
                early = process.Id;
                continue;
            }
            if ((closest - currentProcess.Id) > (process.Id - currentProcess.Id) || closest == 0)
            {
                if (closest != 0) nonunique = true;
                closest = process.Id;
            }
            if (nonunique) Log.Error($"More than one {executableName} found! Selecting the most likely match with process id {closest}.");
        }
        if (closest == 0 && early != 0) closest = early;
        if (closest != 0) Log.Information($"Process for {executableName} found using fallback method: {closest}. XLCore pid: {currentProcess.Id}");
        return closest;
    }

    // ---- Path conversion & registry ----
    public string UnixToWinePath(string unixPath)
    {
        if (_useProtonMode)
        {
            var launchArguments = $"winepath --windows \"{unixPath}\"";
            var winePath = RunWithoutRuntime(launchArguments);
            var output = winePath.StandardOutput.ReadToEnd();
            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        }

        var launchArgumentsLegacy = new string[] { "winepath", "--windows", unixPath };
        var winePathLegacy = RunInPrefix(launchArgumentsLegacy, redirectOutput: true);
        var outputLegacy = winePathLegacy.StandardOutput.ReadToEnd();
        return outputLegacy.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
    }

    public void AddRegistryKey(string key, string value, string data)
    {
        if (_useProtonMode)
        {
            var args = $"reg add \"{key}\" /v \"{value}\" /d \"{data}\" /f";
            var wineProcess = RunWithoutRuntime(args);
            wineProcess.WaitForExit();
            return;
        }

        var legacyArgs = new string[] { "reg", "add", key, "/v", value, "/d", data, "/f" };
        var legacyProcess = RunInPrefix(legacyArgs);
        legacyProcess.WaitForExit();
    }

    // ---- Proton-specific helpers ----
    public void SetWineD3DVulkan(bool useVulkan)
    {
        if (!_useProtonMode) return;

        var renderer = useVulkan ? "vulkan" : "gl";
        var wined3dFile = new FileInfo(Path.Combine(_protonSettings.Prefix.FullName, "xl_wined3d.txt"));
        if (wined3dFile.Exists)
        {
            var current = File.ReadAllText(wined3dFile.FullName);
            if (current.Trim() == renderer)
            {
                Log.Verbose($"[WINEPREFIX] WineD3D renderer is already set to {renderer}");
                return;
            }
        }
        Log.Verbose($"[WINEPREFIX] WineD3D renderer changed to {renderer}");
        File.WriteAllText(wined3dFile.FullName, renderer);
        AddRegistryKey("HKEY_CURRENT_USER\\Software\\Wine\\Direct3D", "renderer", renderer);
    }

    public void SetHideWineExports(bool hide)
    {
        if (!_useProtonMode) return;

        var hidden = hide ? "Y" : "N";
        var hideExportsFile = new FileInfo(Path.Combine(_protonSettings.Prefix.FullName, "xl_hidewineexports.txt"));
        if (hideExportsFile.Exists)
        {
            var current = File.ReadAllText(hideExportsFile.FullName);
            if (current.Trim() == hidden)
            {
                Log.Verbose($"[WINEPREFIX] HideWineExports currently set to {hidden}.");
                return;
            }
        }
        Log.Verbose($"[WINEPREFIX] HideWineExports changed to {hidden}.");
        File.WriteAllText(hideExportsFile.FullName, hidden);
        AddRegistryKey("HKEY_CURRENT_USER\\Software\\Wine\\AppDefaults", "HideWineExports", hidden);
    }

    /// <summary>
    /// Remove nvapi DLLs from the prefix that were previously installed manually.
    /// GE-Proton / proton-cachyos ships its own matching dxvk-nvapi — our separately
    /// downloaded version can conflict and cause a black screen.
    /// </summary>
    private static void CleanupNvapiDlls(DirectoryInfo prefix)
    {
        var system32 = Path.Combine(prefix.FullName, "drive_c", "windows", "system32");
        foreach (var dll in new[] { "nvapi64.dll", "nvapi.dll", "nvapi-srv64.dll", "nvapi-srv.dll" })
        {
            var path = Path.Combine(system32, dll);
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    Log.Verbose($"Removed manually-installed nvapi DLL: {path}");
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, $"Failed to clean up nvapi DLL: {path}");
            }
        }
    }

    // ---- Kill ----
    public void Kill()
    {
        var psi = new ProcessStartInfo(WineServerPathFull)
        {
            Arguments = "-k"
        };
        if (_useProtonMode)
            psi.EnvironmentVariables.Add("WINEPREFIX", _protonSettings.Prefix.FullName);
        else
            psi.EnvironmentVariables.Add("WINEPREFIX", Settings.Prefix.FullName);

        Process.Start(psi);
    }
}
