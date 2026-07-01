using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Newtonsoft.Json;

using Serilog;

using XIVLauncher.Common.Dalamud;
using XIVLauncher.Common.PlatformAbstractions;
using XIVLauncher.Common.Unix.Compatibility;

namespace XIVLauncher.Common.Unix;

public class UnixDalamudRunner : IDalamudRunner
{
    private static readonly string[] KnownGameExes = { "ffxiv_dx11", "ffxiv_dx11.exe", "ffxiv", "ffxiv.exe" };

    private readonly CompatibilityTools compatibility;
    private readonly DirectoryInfo dotnetRuntime;

    public UnixDalamudRunner(CompatibilityTools compatibility, DirectoryInfo dotnetRuntime)
    {
        this.compatibility = compatibility;
        this.dotnetRuntime = dotnetRuntime;
    }

    public Process? Run(FileInfo runner, bool fakeLogin, bool noPlugins, bool noThirdPlugins, FileInfo gameExe, string gameArgs, IDictionary<string, string> environment, DalamudLoadMethod loadMethod, DalamudStartInfo startInfo)
    {
        var gameExePath = "";
        var dotnetRuntimePath = "";

        Parallel.Invoke(
            () => { gameExePath = compatibility.UnixToWinePath(gameExe.FullName); },
            () => { dotnetRuntimePath = compatibility.UnixToWinePath(dotnetRuntime.FullName); },
            () => { startInfo.LoggingPath = compatibility.UnixToWinePath(startInfo.LoggingPath); },
            () => { startInfo.WorkingDirectory = compatibility.UnixToWinePath(startInfo.WorkingDirectory); },
            () => { startInfo.ConfigurationPath = compatibility.UnixToWinePath(startInfo.ConfigurationPath); },
            () => { startInfo.PluginDirectory = compatibility.UnixToWinePath(startInfo.PluginDirectory); },
            () => { startInfo.AssetDirectory = compatibility.UnixToWinePath(startInfo.AssetDirectory); }
        );

        var prevDalamudRuntime = Environment.GetEnvironmentVariable("DALAMUD_RUNTIME");
        if (!string.IsNullOrWhiteSpace(prevDalamudRuntime))
            dotnetRuntimePath = prevDalamudRuntime;

        environment.Add("DALAMUD_RUNTIME", dotnetRuntimePath);
        environment.Add("DOTNET_ROOT", dotnetRuntimePath);
        environment.Add("DOTNET_ROOT_X64", dotnetRuntimePath);
        environment.Add("WINEDOTNET_ROOT", dotnetRuntimePath);

        var launchArguments = new List<string>
        {
            $"\"{runner.FullName}\"",
            DalamudInjectorArgs.LAUNCH,
            DalamudInjectorArgs.Mode(loadMethod == DalamudLoadMethod.EntryPoint ? "entrypoint" : "inject"),
            DalamudInjectorArgs.Game(gameExePath),
            DalamudInjectorArgs.WorkingDirectory(startInfo.WorkingDirectory),
            DalamudInjectorArgs.ConfigurationPath(startInfo.ConfigurationPath),
            DalamudInjectorArgs.LoggingPath(startInfo.LoggingPath),
            DalamudInjectorArgs.PluginDirectory(startInfo.PluginDirectory),
            DalamudInjectorArgs.AssetDirectory(startInfo.AssetDirectory),
            DalamudInjectorArgs.ClientLanguage((int)startInfo.Language),
            DalamudInjectorArgs.DelayInitialize(startInfo.DelayInitializeMs),
            DalamudInjectorArgs.TsPackB64(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(startInfo.TroubleshootingPackData))),
        };

        if (loadMethod == DalamudLoadMethod.ACLonly)
            launchArguments.Add(DalamudInjectorArgs.WITHOUT_DALAMUD);

        if (fakeLogin)
            launchArguments.Add(DalamudInjectorArgs.FAKE_ARGUMENTS);

        if (noPlugins)
            launchArguments.Add(DalamudInjectorArgs.NO_PLUGIN);

        if (noThirdPlugins)
            launchArguments.Add(DalamudInjectorArgs.NO_THIRD_PARTY);

        launchArguments.Add("--");
        launchArguments.Add(gameArgs);

        // Use RunTheGame like RB does — critical for Proton/UMU mode
        var dalamudProcess = compatibility.RunTheGame(string.Join(" ", launchArguments), environment: environment, redirectOutput: true, writeLog: true);

        DalamudConsoleOutput dalamudConsoleOutput = null;
        int invalidJsonCount = 0;

        // Keep checking for valid json output, but only 5 times.
        // If it's still erroring out at that point, fall back to process name lookup.
        while (dalamudConsoleOutput == null && invalidJsonCount < 5)
        {
            var output = dalamudProcess.StandardOutput.ReadLine();

            if (output == null)
            {
                Log.Warning("Dalamud injector produced no stdout output; trying fallback process lookup");
                return FindGameProcessByPolling();
            }

            Console.WriteLine(output);

            try
            {
                dalamudConsoleOutput = JsonConvert.DeserializeObject<DalamudConsoleOutput>(output);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, $"Couldn't parse Dalamud output: {output}");
            }

            invalidJsonCount++;
        }

        // Drain remaining output in background thread (same as RB)
        new Thread(() =>
        {
            while (!dalamudProcess.StandardOutput.EndOfStream)
            {
                var output = dalamudProcess.StandardOutput.ReadLine();
                if (output != null)
                    Console.WriteLine(output);
            }
        }).Start();

        try
        {
            var unixPid = compatibility.GetUnixProcessId(dalamudConsoleOutput.Pid);

            if (unixPid == 0)
            {
                Log.Error("Could not retrieve Unix process ID; trying fallback process lookup");
                return FindGameProcessByPolling();
            }

            var gameProcess = Process.GetProcessById(unixPid);
            Log.Verbose($"Got game process handle {gameProcess.Handle} with Unix pid {gameProcess.Id} and Wine pid {dalamudConsoleOutput.Pid}");
            return gameProcess;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not retrieve game Process information; trying fallback process lookup");
            return FindGameProcessByPolling();
        }
    }

    /// <summary>
    /// Fallback: poll for the game process by known executable names.
    /// Used when the Dalamud injector's stdout can't be read (e.g. under UMU/pressure-vessel).
    /// </summary>
    private static Process? FindGameProcessByPolling()
    {
        var currentPid = Environment.ProcessId;
        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (DateTime.UtcNow < deadline)
        {
            foreach (var exeName in KnownGameExes)
            {
                try
                {
                    var processes = Process.GetProcessesByName(exeName)
                        .Concat(Process.GetProcessesByName(exeName.Replace(".exe", "")))
                        .DistinctBy(p => p.Id)
                        .ToList();

                    var match = processes
                        .Where(p => p.Id != currentPid)
                        .Where(p =>
                        {
                            try { return p.StartTime > Process.GetCurrentProcess().StartTime; }
                            catch { return true; }
                        })
                        .OrderByDescending(p =>
                        {
                            try { return p.StartTime.Ticks; }
                            catch { return 0L; }
                        })
                        .FirstOrDefault();

                    if (match != null)
                    {
                        Log.Information("Game process found by polling: {ExeName} pid {Pid}", exeName, match.Id);
                        return match;
                    }
                }
                catch (Exception ex)
                {
                    Log.Verbose(ex, "Error while polling for {ExeName}", exeName);
                }
            }

            Thread.Sleep(1000);
        }

        Log.Error("Could not find game process within 30 seconds");
        return null;
    }
}
