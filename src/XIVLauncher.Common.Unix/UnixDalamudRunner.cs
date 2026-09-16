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

        // The injector is supposed to print a JSON line with the Wine PID of the
        // game, which we then translate to a Unix PID. Under the umu-launcher /
        // pressure-vessel sandbox that pipe is not connected: it stays silent and
        // only reports EOF once the whole wrapper (and therefore the game) has
        // exited. Reading it inline meant we only started looking for the game
        // after it was already gone. Read it on a background thread and look for
        // the game process in parallel instead.
        var injectorOutput = new InjectorOutput();
        new Thread(() => ReadInjectorOutput(dalamudProcess, injectorOutput))
        {
            IsBackground = true,
            Name = "Dalamud injector stdout",
        }.Start();

        Process? gameProcess = null;
        DalamudConsoleOutput? handledOutput = null;
        var stdoutFallbackLogged = false;
        var deadline = DateTime.UtcNow.AddSeconds(PROCESS_LOOKUP_TIMEOUT_SECONDS);

        while (gameProcess is null && DateTime.UtcNow < deadline)
        {
            var consoleOutput = injectorOutput.Value;

            if (consoleOutput is not null && !ReferenceEquals(consoleOutput, handledOutput))
            {
                handledOutput = consoleOutput;
                gameProcess = this.GetGameProcessFromWinePid(consoleOutput.Pid);
            }
            else
            {
                if (injectorOutput.Closed && !stdoutFallbackLogged)
                {
                    Log.Warning("Dalamud injector produced no stdout output; trying fallback process lookup");
                    stdoutFallbackLogged = true;
                }

                gameProcess = FindGameProcessByName();
            }

            if (gameProcess is null)
                Thread.Sleep(1000);
        }

        if (gameProcess is null && !HasExited(dalamudProcess))
        {
            // We could not identify the actual game process (which can happen
            // when the sandbox hides it and its name cannot be resolved), but the
            // wrapper launched with "waitforexitandrun" tracks the lifetime of
            // the game. Waiting on it still closes the launcher when the game
            // ends instead of leaving it behind.
            Log.Warning("Could not identify the game process; using the launcher wrapper process to track the game");
            gameProcess = dalamudProcess;
        }
        else if (gameProcess is null)
        {
            var exited = HasExited(dalamudProcess);
            var exitCode = exited ? TryGetExitCode(dalamudProcess) : null;

            Log.Error(
                "Could not find game process within {Timeout} seconds (injector wrapper exited: {Exited}, exit code: {ExitCode})",
                PROCESS_LOOKUP_TIMEOUT_SECONDS,
                exited,
                exitCode?.ToString() ?? "n/a");
        }

        return gameProcess;
    }

    private const int PROCESS_LOOKUP_TIMEOUT_SECONDS = 30;

    private static int? TryGetExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch
        {
            return null;
        }
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Translates the Wine PID reported by the injector into a Unix process.
    /// Returns null when the PID cannot be mapped or the process is gone.
    /// </summary>
    private Process? GetGameProcessFromWinePid(int winePid)
    {
        try
        {
            var unixPid = this.compatibility.GetUnixProcessId(winePid);

            if (unixPid == 0)
            {
                Log.Error("Could not retrieve Unix process ID; trying fallback process lookup");
                return null;
            }

            var gameProcess = Process.GetProcessById(unixPid);
            Log.Verbose($"Got game process with Unix pid {gameProcess.Id} and Wine pid {winePid}");
            return gameProcess;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not retrieve game Process information; trying fallback process lookup");
            return null;
        }
    }

    /// <summary>
    /// Reads the injector's stdout, looking for the JSON line that reports the
    /// Wine PID of the launched game. Runs on its own thread so the process
    /// lookup is never blocked by a pipe that may only be closed on exit.
    /// </summary>
    private static void ReadInjectorOutput(Process dalamudProcess, InjectorOutput output)
    {
        try
        {
            // Keep checking for valid json output, but only 5 times.
            // If it's still erroring out at that point, the lookup falls back
            // to the process name scan.
            var invalidJsonCount = 0;

            while (invalidJsonCount < 5)
            {
                var line = dalamudProcess.StandardOutput.ReadLine();

                if (line == null)
                    break;

                Console.WriteLine(line);

                try
                {
                    var parsed = JsonConvert.DeserializeObject<DalamudConsoleOutput>(line);

                    if (parsed != null)
                    {
                        output.Value = parsed;
                        break;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, $"Couldn't parse Dalamud output: {line}");
                }

                invalidJsonCount++;
            }

            // Drain remaining output so the injector never blocks on a full pipe.
            while (!dalamudProcess.StandardOutput.EndOfStream)
            {
                var line = dalamudProcess.StandardOutput.ReadLine();

                if (line != null)
                    Console.WriteLine(line);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not read the Dalamud injector output");
        }
        finally
        {
            output.Closed = true;
        }
    }

    /// <summary>
    /// Fallback: look for the game process by known executable names.
    /// Used when the Dalamud injector's stdout can't be read (e.g. under UMU/pressure-vessel).
    /// </summary>
    private static Process? FindGameProcessByName()
    {
        var currentPid = Environment.ProcessId;
        var launcherStartTime = GetSafeStartTime(Process.GetCurrentProcess());

        foreach (var exeName in KnownGameExes)
        {
            var processes = new Dictionary<int, Process>();

            try
            {
                foreach (var process in Process.GetProcessesByName(exeName)
                             .Concat(Process.GetProcessesByName(exeName.Replace(".exe", ""))))
                {
                    if (processes.ContainsKey(process.Id))
                    {
                        process.Dispose();
                        continue;
                    }

                    processes.Add(process.Id, process);
                }
            }
            catch (Exception ex)
            {
                Log.Verbose(ex, "Error while polling for {ExeName}", exeName);

                foreach (var process in processes.Values)
                    process.Dispose();

                continue;
            }

            Process? match = null;

            try
            {
                match = processes.Values
                    .Where(p => p.Id != currentPid)
                    .Where(p =>
                    {
                        var startTime = GetSafeStartTime(p);
                        return startTime is null || launcherStartTime is null || startTime > launcherStartTime;
                    })
                    .OrderByDescending(p => GetSafeStartTime(p) ?? DateTime.MinValue)
                    .FirstOrDefault();
            }
            catch (Exception ex)
            {
                Log.Verbose(ex, "Error while polling for {ExeName}", exeName);
            }

            foreach (var process in processes.Values)
            {
                if (!ReferenceEquals(process, match))
                    process.Dispose();
            }

            if (match != null)
            {
                Log.Information("Game process found by polling: {ExeName} pid {Pid}", exeName, match.Id);
                return match;
            }
        }

        return null;
    }

    private static DateTime? GetSafeStartTime(Process process)
    {
        try
        {
            return process.StartTime;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Thread-safe holder for the JSON output produced by the injector.
    /// </summary>
    private sealed class InjectorOutput
    {
        private readonly object gate = new();
        private DalamudConsoleOutput? value;
        private bool closed;

        public DalamudConsoleOutput? Value
        {
            get { lock (this.gate) return this.value; }
            set { lock (this.gate) this.value = value; }
        }

        public bool Closed
        {
            get { lock (this.gate) return this.closed; }
            set { lock (this.gate) this.closed = value; }
        }
    }
}
