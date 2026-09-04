using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using XIVLauncher.Common.Dalamud;
using XIVLauncher.Common.PlatformAbstractions;
using XIVLauncher.Common.Unix.Compatibility;

namespace XIVLauncher.Common.Unix;

public class UnixGameRunner : IGameRunner
{
    private readonly CompatibilityTools compatibility;
    private readonly DalamudLauncher dalamudLauncher;
    private readonly bool dalamudOk;

    public UnixGameRunner(CompatibilityTools compatibility, DalamudLauncher dalamudLauncher, bool dalamudOk)
    {
        this.compatibility = compatibility;
        this.dalamudLauncher = dalamudLauncher;
        this.dalamudOk = dalamudOk;
    }

    /// <summary>
    /// The game itself must run through Proton with the "waitforexitandrun"
    /// verb, exactly like Steam and the umu-launcher default do. GE-Proton only
    /// runs its per-launch prefix setup (setup_prefix) for that verb; that setup
    /// is what appends the vkd3d-proton (d3d12, d3d12core), DXVK (d3d11, dxgi,
    /// d3d9, d3d10core) and dxvk-nvapi (nvapi64, ...) DLL overrides to
    /// WINEDLLOVERRIDES. With "runinprefix" (kept for helper commands such as
    /// winedbg) those overrides are never applied, so the game falls back to
    /// Wine's builtin d3d12/d3d12core and D3D12CreateDevice on a DXVK adapter
    /// fails with E_NOINTERFACE (0x80004002) — e.g. OptiScaler's DX12 path.
    /// </summary>
    private const string GAME_PROTON_VERB = "waitforexitandrun";

    public Process? Start(string path, string workingDirectory, string arguments, IDictionary<string, string> environment, DpiAwareness dpiAwareness)
    {
        // Build the game environment from the caller's dictionary without
        // mutating it, then force the game launch verb (merged last, so it wins
        // over the runinprefix default in WineSettings.EnvVars).
        var gameEnvironment = new Dictionary<string, string>();
        if (environment != null)
        {
            foreach (var kv in environment)
                gameEnvironment[kv.Key] = kv.Value;
        }

        gameEnvironment["PROTON_VERB"] = GAME_PROTON_VERB;

        if (dalamudOk)
        {
            return this.dalamudLauncher.Run(new FileInfo(path), arguments, gameEnvironment);
        }
        else
        {
            return compatibility.RunInPrefix($"\"{path}\" {arguments}", workingDirectory, gameEnvironment, writeLog: true);
        }
    }
}
