using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Serilog;
using XIVLauncher.Common.Util;

namespace XIVLauncher.Common.Unix.Compatibility;

/// <summary>
/// Compatibility tool manager for the Proton + umu-launcher setup.
///
/// Proton is always launched through the umu launcher (umu-run). The umu
/// launcher replicates Steam's compatibility environment (prefix layout,
/// container runtime, mounts, …) so that Proton's own DXVK/NVAPI behave as
/// they do under Steam. No standalone DXVK / dxvk-nvapi is ever installed
/// over the prefix — everything is provided by the Proton build itself.
/// </summary>
public class CompatibilityTools
{
    private readonly Wine.WineSettings settings;
    private readonly DxvkHudType hudType;
    private readonly bool gamemodeOn;
    private readonly bool dxvkAsyncOn;
    private readonly bool gplAsyncCacheOn;
    private readonly int frameRate;
    private readonly DirectoryInfo umuDirectory;
    private readonly StreamWriter logWriter;

    public bool IsToolReady { get; private set; }

    public DirectoryInfo Prefix => settings.Prefix;

    public bool IsToolDownloaded => File.Exists(RuntimePath) && File.Exists(WinePath) && settings.Prefix.Exists;

    /// <summary>
    /// The proton script (used for existence checks and to locate wineserver).
    /// </summary>
    private string WinePath => settings.WinePath;

    private string WineServerPath => settings.WineServerPath;

    /// <summary>
    /// Proton is always run through the umu launcher, never by calling the
    /// proton script directly.
    /// </summary>
    private string RuntimePath => settings.UmuLauncher.Name;

    public CompatibilityTools(Wine.WineSettings wineSettings, int frameRate, DxvkHudType hudType, bool gamemodeOn, bool dxvkAsyncOn, bool gplAsyncCacheOn)
    {
        if (wineSettings is null)
            throw new ArgumentNullException(nameof(wineSettings));

        this.settings = wineSettings;
        this.frameRate = (frameRate == 0 || frameRate >= 30) ? frameRate : 0;
        this.hudType = hudType;
        this.gamemodeOn = gamemodeOn;
        this.dxvkAsyncOn = dxvkAsyncOn;
        this.gplAsyncCacheOn = gplAsyncCacheOn;
        this.umuDirectory = new DirectoryInfo(Path.Combine(wineSettings.Paths.StorageFolder.FullName, "compatibilitytool", "umu"));

        this.umuDirectory.Create();

        this.logWriter = new StreamWriter(wineSettings.LogFile.FullName);

        if (!wineSettings.Prefix.Exists)
            wineSettings.Prefix.Create();

        // Do proton prefixes like umu, with pfx symlinked back to prefix folder.
        var pfx = new FileInfo(Path.Combine(wineSettings.Prefix.FullName, "pfx"));
        if (pfx.Exists)
        {
            if (pfx.ResolveLinkTarget(false) is null)
            {
                pfx.Delete();
                pfx.CreateAsSymbolicLink(wineSettings.Prefix.FullName);
            }
            else if (pfx.ResolveLinkTarget(true).FullName != wineSettings.Prefix.FullName)
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

    // ---- EnsureTool ----
    public async Task EnsureTool(DirectoryInfo tempPath)
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("User-Agent", PlatformHelpers.GetVersion());
        await EnsureTool(client, tempPath).ConfigureAwait(false);
    }

    public async Task EnsureTool(HttpClient httpClient, DirectoryInfo tempPath)
    {
        // Download the umu launcher if it's missing or out of date.
        var downloadUrlExists = !string.IsNullOrEmpty(settings.UmuLauncher.DownloadUrl);
        if (!File.Exists(RuntimePath) && !downloadUrlExists)
            throw new ArgumentNullException("Umu Launcher selected, but is not present, and no download url provided.");

        if (downloadUrlExists)
        {
            var downloadUmu = false;
            var urlParts = settings.UmuLauncher.DownloadUrl.Split('/');
            var webVersion = urlParts[urlParts.Length - 2]; // The version is the second to last part of the url.

            var versionFile = new FileInfo(Path.Combine(umuDirectory.FullName, "version"));
            if (versionFile.Exists)
            {
                var currentVersion = File.ReadAllText(versionFile.FullName).Trim();
                if (currentVersion != webVersion)
                {
                    downloadUmu = true;
                    Log.Information($"[UMU] Umu Launcher version mismatch. Current version: {currentVersion}, expected version: {webVersion}. Downloading...");
                }
            }
            else
            {
                downloadUmu = true;
                Log.Information($"[UMU] Umu Launcher version file not found. Expected at {versionFile.FullName}. Downloading...");
            }

            if (downloadUmu)
            {
                if (string.IsNullOrEmpty(settings.UmuLauncher.DownloadUrl))
                    throw new ArgumentNullException("Umu Launcher selected, but is not present, and no download url provided.");
                umuDirectory.Delete(true);
                umuDirectory.Create();
                Log.Information($"[UMU] umu-run is not in $PATH, downloading {settings.UmuLauncher.DownloadUrl} to {umuDirectory.FullName}");
                // Download the runtime to the parent folder of umuDirectory, since it will create a folder called "umu" inside it.
                await Wine.Runtime.DownloadRuntime(httpClient, umuDirectory.Parent, settings.UmuLauncher.DownloadUrl).ConfigureAwait(false);
                File.WriteAllText(versionFile.FullName, webVersion);
            }
        }

        // Download the selected Proton build if it's missing.
        if (!File.Exists(WinePath))
        {
            if (string.IsNullOrEmpty(settings.WineRelease.DownloadUrl))
                throw new ArgumentNullException($"Proton not found at {WinePath}, and no download url provided.");
            Log.Information($"{settings.WineRelease.Label} does not exist. Downloading {settings.WineRelease.DownloadUrl} to {settings.WineRelease.ParentFolder}");
            await DownloadTool(httpClient, new DirectoryInfo(settings.WineRelease.ParentFolder), tempPath).ConfigureAwait(false);
        }

        EnsurePrefix();

        IsToolReady = true;
    }

    private async Task DownloadTool(HttpClient httpClient, DirectoryInfo targetPath, DirectoryInfo tempPath)
    {
        var tempFilePath = Path.Combine(tempPath.FullName, $"{Guid.NewGuid()}");
        await File.WriteAllBytesAsync(tempFilePath, await httpClient.GetByteArrayAsync(settings.WineRelease.DownloadUrl).ConfigureAwait(false)).ConfigureAwait(false);
        if (!Wine.CompatUtil.EnsureChecksumMatch(tempFilePath, settings.WineRelease.Checksums))
        {
            throw new InvalidDataException("SHA512 checksum verification failed");
        }
        PlatformHelpers.Untar(tempFilePath, targetPath.FullName);
        Log.Information("Compatibility tool {Name} successfully extracted to {Path}", settings.WineRelease.Label, targetPath.FullName);
        File.Delete(tempFilePath);
    }

    // ---- Prefix management ----
    private void ResetPrefix()
    {
        settings.Prefix.Refresh();
        if (settings.Prefix.Exists)
            settings.Prefix.Delete(true);
        settings.Prefix.Create();
        EnsurePrefix();
    }

    public void EnsurePrefix()
    {
        // Delete lsteamclient.dll to prevent crashes.
        var lsteamclient = new FileInfo(Path.Combine(settings.Prefix.FullName, "drive_c", "windows", "system32", "lsteamclient.dll"));
        if (lsteamclient.Exists)
        {
            lsteamclient.Delete();
            Log.Verbose("Using custom wine or non-lsteamclient wine. Deleting lsteamclient.dll from prefix.");
        }

        var verb = "runinprefix";
        if (!File.Exists(Path.Combine(settings.Prefix.FullName, "config_info")) &&
            !File.Exists(Path.Combine(settings.Prefix.FullName, "pfx.lock")) &&
            !File.Exists(Path.Combine(settings.Prefix.FullName, "tracked_files")) &&
            !File.Exists(Path.Combine(settings.Prefix.FullName, "version")))
        {
            verb = "run";
        }
        RunWithoutRuntime("cmd /c dir %userprofile%/Documents > nul", verb, false).WaitForExit();
    }

    // ---- RunInPrefix variants ----
    /// <summary>
    /// Runs a helper command through the umu launcher without the game wrapper.
    /// The proton verb is passed through the PROTON_VERB environment variable,
    /// which the umu launcher translates into the proton verb.
    /// </summary>
    public Process RunWithoutRuntime(string command, string verb = "runinprefix", bool redirect = true)
    {
        var psi = new ProcessStartInfo(RuntimePath);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        MergeDictionaries(psi.EnvironmentVariables, settings.EnvVars);
        psi.EnvironmentVariables["PROTON_VERB"] = verb;
        psi.Environment.Add("WINEDLLOVERRIDES", settings.WineDLLOverrides + "n,b");
        psi.Arguments = command;
        var quickRun = new Process();
        quickRun.StartInfo = psi;
        quickRun.Start();
        Log.Verbose("Running without runtime: {FileName} {Arguments}", psi.FileName, psi.Arguments);
        return quickRun;
    }

    public Process RunTheGame(string command, string workingDirectory = "", IDictionary<string, string> environment = null, bool redirectOutput = false, bool writeLog = false, bool wineD3D = false)
    {
        var psi = new ProcessStartInfo(RuntimePath);
        psi.Arguments = command;
        return RunInPrefix(psi, workingDirectory, environment, redirectOutput, writeLog, wineD3D);
    }

    public Process RunInPrefix(string command, string workingDirectory = "", IDictionary<string, string> environment = null, bool redirectOutput = false, bool writeLog = false, bool wineD3D = false)
    {
        var psi = new ProcessStartInfo(RuntimePath);
        psi.Arguments = command;
        Log.Verbose("Running in prefix: {FileName} {Arguments}", psi.FileName, psi.Arguments);
        return RunInPrefix(psi, workingDirectory, environment, redirectOutput, writeLog, wineD3D);
    }

    public Process RunInPrefix(string[] args, string workingDirectory = "", IDictionary<string, string> environment = null, bool redirectOutput = false, bool writeLog = false, bool wineD3D = false)
    {
        var psi = new ProcessStartInfo(RuntimePath);
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        Log.Verbose("Running in prefix: {FileName} {Arguments}", psi.FileName, psi.ArgumentList.Aggregate(string.Empty, (a, b) => a + " " + b));
        return RunInPrefix(psi, workingDirectory, environment, redirectOutput, writeLog, wineD3D);
    }

    private void MergeDictionaries(StringDictionary a, IDictionary<string, string> b)
    {
        if (b is null)
            return;

        foreach (var keyValuePair in b)
        {
            if (a.ContainsKey(keyValuePair.Key))
            {
                if (keyValuePair.Key == "LD_PRELOAD")
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

        var wineEnvironmentVariables = new Dictionary<string, string>();

        // DXVK comes from the Proton build itself. wineD3D forces Proton's
        // wined3d (builtin) instead of its bundled DXVK.
        var ogl = wineD3D;

        wineEnvironmentVariables.Add("WINEDLLOVERRIDES", settings.WineDLLOverrides + (ogl ? "b" : "n,b"));

        if (!ogl && frameRate > 0)
            wineEnvironmentVariables.Add("DXVK_FRAME_RATE", frameRate.ToString());

        if (!string.IsNullOrEmpty(settings.DebugVars))
            wineEnvironmentVariables.Add("WINEDEBUG", settings.DebugVars);

        wineEnvironmentVariables.Add("XL_WINEONLINUX", "true");

        string ldPreload = Environment.GetEnvironmentVariable("LD_PRELOAD") ?? "";

        var dxvkHud = hudType switch
        {
            DxvkHudType.Fps => "fps",
            DxvkHudType.Full => "full",
            _ => "",
        };

        if (!string.IsNullOrEmpty(dxvkHud))
            wineEnvironmentVariables.Add("DXVK_HUD", dxvkHud);

        if (gamemodeOn && !ldPreload.Contains("libgamemodeauto.so.0"))
        {
            ldPreload = ldPreload == "" ? "libgamemodeauto.so.0" : ldPreload + ":libgamemodeauto.so.0";
        }

        if (dxvkAsyncOn)
        {
            wineEnvironmentVariables.Add("DXVK_ASYNC", "1");
            wineEnvironmentVariables.Add("DXVK_GPLASYNCCACHE", gplAsyncCacheOn ? "1" : "0");
        }

        wineEnvironmentVariables.Add("LD_PRELOAD", ldPreload);

        MergeDictionaries(psi.EnvironmentVariables, wineEnvironmentVariables);
        MergeDictionaries(psi.EnvironmentVariables, settings.EnvVars);
        MergeDictionaries(psi.EnvironmentVariables, environment);

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
        // For Proton mode, try winedbg first (works with XIV Proton),
        // then fall back to direct process name lookup.
        var pid = TryGetUnixPidFromWineDbgProcmap(winePid);
        if (pid != 0)
            return pid;

        Log.Warning("winedbg info procmap failed for Proton mode, trying direct process lookup");
        return FindGameProcessByName();
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

    // ---- Path conversion & registry ----
    public string UnixToWinePath(string unixPath)
    {
        var launchArguments = $"winepath --windows \"{unixPath}\"";
        var winePath = RunWithoutRuntime(launchArguments);
        var output = winePath.StandardOutput.ReadToEnd();
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
    }

    public void AddRegistryKey(string key, string value, string data)
    {
        var args = $"reg add \"{key}\" /v \"{value}\" /d \"{data}\" /f";
        var wineProcess = RunWithoutRuntime(args);
        wineProcess.WaitForExit();
    }

    // ---- Proton-specific helpers ----
    public void SetWineD3DVulkan(bool useVulkan)
    {
        var renderer = useVulkan ? "vulkan" : "gl";
        var wined3dFile = new FileInfo(Path.Combine(settings.Prefix.FullName, "xl_wined3d.txt"));
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
        var hidden = hide ? "Y" : "N";
        var hideExportsFile = new FileInfo(Path.Combine(settings.Prefix.FullName, "xl_hidewineexports.txt"));
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

    // ---- Kill ----
    public void Kill()
    {
        var psi = new ProcessStartInfo(WineServerPath)
        {
            Arguments = "-k"
        };
        psi.EnvironmentVariables.Add("WINEPREFIX", settings.Prefix.FullName);

        Process.Start(psi);
    }
}
