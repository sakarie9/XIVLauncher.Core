using System.Numerics;
using System.Runtime.InteropServices;

using CheapLoc;

using Config.Net;

using ImGuiNET;

using Serilog;

using Veldrid;
using Veldrid.Sdl2;
using Veldrid.StartupUtilities;

using XIVLauncher.Common;
using XIVLauncher.Common.Dalamud;
using XIVLauncher.Common.Game.Patch;
using XIVLauncher.Common.Game.Patch.Acquisition;
using XIVLauncher.Common.PlatformAbstractions;
using XIVLauncher.Common.Support;
using XIVLauncher.Common.Unix;
using XIVLauncher.Common.Unix.Compatibility;
using XIVLauncher.Common.Unix.Compatibility.Wine;
using XIVLauncher.Common.Unix.Compatibility.Wine.Releases;
using XIVLauncher.Common.Util;
using XIVLauncher.Common.Windows;
using XIVLauncher.Core.Accounts;
using XIVLauncher.Core.Accounts.Cred;
using XIVLauncher.Core.Accounts.Secrets;
using XIVLauncher.Core.Accounts.Secrets.Providers;
using XIVLauncher.Core.Components.LoadingPage;
using XIVLauncher.Core.Configuration;
using XIVLauncher.Core.Configuration.Parsers;
using XIVLauncher.Core.Style;

namespace XIVLauncher.Core;

sealed class Program
{
    private static Sdl2Window window = null!;
    private static CommandList cl = null!;
    private static GraphicsDevice gd = null!;
    private static ImGuiBindings bindings = null!;

    public static GraphicsDevice GraphicsDevice => gd;
    public static ImGuiBindings ImGuiBindings => bindings;
    public static ILauncherConfig Config { get; private set; } = null!;
    public static CommonSettings CommonSettings => new(Config);
    public static ISteam? Steam { get; private set; }
    public static DalamudUpdater DalamudUpdater { get; private set; } = null!;
    public static DalamudOverlayInfoProxy DalamudLoadInfo { get; private set; } = null!;
    public static CompatibilityTools CompatibilityTools { get; private set; } = null!;
    public static WineManager WineManager { get; private set; } = null!;

    public static AccountManager AccountManager => launcherApp.Accounts;

    private static readonly Lazy<HttpClient> _httpClient = new Lazy<HttpClient>(() =>
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        client.DefaultRequestHeaders.Add("User-Agent", PlatformHelpers.GetVersion());

        return client;
    });

    public static HttpClient HttpClient => _httpClient.Value;
    public static PatchManager Patcher { get; set; } = null!;

    private static readonly Vector3 ClearColor = new(0.1f, 0.1f, 0.1f);

    private static LauncherApp launcherApp = null!;
    public static Storage storage = null!;
    public static DirectoryInfo DotnetRuntime => storage.GetFolder("runtime");

    // TODO: We don't have the steamworks api for this yet.
    public static bool IsSteamDeckHardware => CoreEnvironmentSettings.IsDeck.HasValue ?
        CoreEnvironmentSettings.IsDeck.Value :
        Directory.Exists("/home/deck") || (CoreEnvironmentSettings.IsDeckGameMode ?? false) || (CoreEnvironmentSettings.IsDeckFirstRun ?? false);
    public static bool IsSteamDeckGamingMode => CoreEnvironmentSettings.IsDeckGameMode.HasValue ?
        CoreEnvironmentSettings.IsDeckGameMode.Value :
        Steam != null && Steam.IsValid && Steam.IsRunningOnSteamDeck();

    private const string APP_NAME = "xlcore_cn";

    private static string[] mainArgs = { };

    private static uint invalidationFrames = 0;
    private static Vector2 lastMousePosition = Vector2.Zero;


    public static string CType = CoreEnvironmentSettings.GetCType();

    public static void Invalidate(uint frames = 100)
    {
        invalidationFrames = frames;
    }

    private static void SetupLogging(string[] args)
    {
        LogInit.Setup(Path.Combine(storage.GetFolder("logs").FullName, "launcher.log"), args);

        Log.Information("========================================================");
        Log.Information("Starting a session(v{Version} - {Hash})", AppUtil.GetAssemblyVersion(), AppUtil.GetGitHash());
    }

    private static void LoadConfig(Storage storage)
    {
        Config = new ConfigurationBuilder<ILauncherConfig>()
                 .UseCommandLineArgs()
                 .UseIniFile(storage.GetFile("launcher.ini").FullName)
                 .UseTypeParser(new DirectoryInfoParser())
                 .UseTypeParser(new AddonListParser())
                 .Build();

        if (string.IsNullOrEmpty(Config.AcceptLanguage))
        {
            Config.AcceptLanguage = ApiHelpers.GenerateAcceptLanguage();
        }

        Config.GamePath ??= storage.GetFolder("ffxiv");
        Config.GameConfigPath ??= storage.GetFolder("ffxivConfig");
        Config.ClientLanguage ??= ClientLanguage.ChineseSimplified;
        Config.DpiAwareness ??= DpiAwareness.Unaware;
        Config.IsAutologin ??= false;
        Config.CompletedFts ??= false;
        Config.DoVersionCheck ??= true;
        Config.FontPxSize ??= 22.0f;

        Config.IsEncryptArgs ??= true;
        Config.IsFt ??= false;
        Config.IsOtpServer ??= false;
        Config.IsIgnoringSteam = CoreEnvironmentSettings.UseSteam.HasValue ? !CoreEnvironmentSettings.UseSteam.Value : Config.IsIgnoringSteam ?? false;

        Config.PatchPath ??= storage.GetFolder("patch");
        Config.PatchAcquisitionMethod ??= AcquisitionMethod.Aria;

        Config.DalamudEnabled ??= true;
        Config.DalamudLoadMethod ??= DalamudLoadMethod.EntryPoint;

        Config.GlobalScale ??= 1.0f;

        Config.GameModeEnabled ??= false;
        Config.DxvkAsyncEnabled ??= true;

        Config.WineDebugVars ??= "-all";

        Config.RB_WineStartupType ??= RBWineStartupType.Proton;
        Config.RB_WineBinaryPath ??= "/usr/bin";
        Config.RB_WineSync ??= RBWineSyncType.FSync;
        Config.RB_UmuLauncher ??= RBUmuLauncherType.System;
        Config.RB_DxvkFrameRate ??= 0;

        // The umu launcher is mandatory for Proton — a legacy "Disabled" value
        // is treated as System so old configs keep working.
        if (Config.RB_UmuLauncher == RBUmuLauncherType.Disabled)
            Config.RB_UmuLauncher = RBUmuLauncherType.System;

        // Only Proton (built-in or custom Proton path) is supported now. Any
        // "Custom" mode that does not point at a directory containing a
        // "proton" executable (e.g. old configs pointing at wine binaries) is
        // migrated back to the managed Proton mode.
        if (Config.RB_WineStartupType == RBWineStartupType.Custom &&
            (string.IsNullOrEmpty(Config.RB_WineBinaryPath) ||
             !WineSettings.IsValidProtonBinaryPath(Config.RB_WineBinaryPath)))
        {
            Log.Warning($"[PROTON] Custom path \"{Config.RB_WineBinaryPath}\" is not a valid Proton directory. Falling back to managed Proton.");
            Config.RB_WineStartupType = RBWineStartupType.Proton;
        }

        Config.FixLDP ??= false;
        Config.FixIM ??= false;
        Config.FixLocale ??= false;
        Config.FixError127 ??= false;
    }

    public const uint STEAM_APP_ID = 39210;
    public const uint STEAM_APP_ID_FT = 312060;

    /// <summary>
    ///     The name of the Dalamud injector executable file.
    /// </summary>
    // TODO: move this somewhere better.
    public const string DALAMUD_INJECTOR_NAME = "Dalamud.Injector.exe";

    /// <summary>
    ///     Creates a new instance of the Dalamud updater.
    /// </summary>
    /// <remarks>
    ///     If <see cref="ILauncherConfig.DalamudManualInjectionEnabled"/> is true and there is an injector at <see cref="ILauncherConfig.DalamudManualInjectPath"/> then
    ///     manual injection will be used instead of a Dalamud branch.
    /// </remarks>
    /// <returns>A <see cref="DalamudUpdater"/> instance.</returns>
    private static DalamudUpdater CreateDalamudUpdater()
    {
        FileInfo runnerOverride = null;
        if (Config.DalamudManualInjectPath is not null &&
            Config.DalamudManualInjectionEnabled == true &&
            Config.DalamudManualInjectPath.Exists &&
            Config.DalamudManualInjectPath.GetFiles().FirstOrDefault(x => x.Name == DALAMUD_INJECTOR_NAME) is not null)
        {
            runnerOverride = new FileInfo(Path.Combine(Config.DalamudManualInjectPath.FullName, DALAMUD_INJECTOR_NAME));
        }
        return new DalamudUpdater(storage.GetFolder("dalamud"), storage.GetFolder("runtime"), storage.GetFolder("dalamudAssets"), storage.Root, null, null)
        {
            Overlay = DalamudLoadInfo,
            RunnerOverride = runnerOverride
        };
    }

    private static void Main(string[] args)
    {
        mainArgs = args;
        storage = new Storage(APP_NAME);

        if (CoreEnvironmentSettings.ClearAll)
        {
            ClearAll();
        }
        else
        {
            if (CoreEnvironmentSettings.ClearSettings) ClearSettings();
            if (CoreEnvironmentSettings.ClearPrefix) ClearPrefix();
            if (CoreEnvironmentSettings.ClearPlugins) ClearPlugins();
            if (CoreEnvironmentSettings.ClearTools) ClearTools();
            if (CoreEnvironmentSettings.ClearLogs) ClearLogs();
        }

        SetupLogging(mainArgs);

        // Initialize the Proton/Wine and umu-launcher manager
        if (Environment.OSVersion.Platform == PlatformID.Unix && RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            WineManager = new WineManager(storage.Root.FullName);
        }

        LoadConfig(storage);

        Loc.SetupWithFallbacks();

        Dictionary<uint, string> apps = new Dictionary<uint, string>();
        uint[] ignoredIds = { 0, STEAM_APP_ID, STEAM_APP_ID_FT };
        if (!ignoredIds.Contains(CoreEnvironmentSettings.SteamAppId))
        {
            apps.Add(CoreEnvironmentSettings.SteamAppId, "XLM");
        }
        if (!ignoredIds.Contains(CoreEnvironmentSettings.AltAppID))
        {
            apps.Add(CoreEnvironmentSettings.AltAppID, "XL_APPID");
        }
        if (Config.IsFt == true)
        {
            apps.Add(STEAM_APP_ID_FT, "FFXIV Free Trial");
            apps.Add(STEAM_APP_ID, "FFXIV Retail");
        }
        else
        {
            apps.Add(STEAM_APP_ID, "FFXIV Retail");
            apps.Add(STEAM_APP_ID_FT, "FFXIV Free Trial");
        }
        try
        {
            switch (Environment.OSVersion.Platform)
            {
                case PlatformID.Win32NT:
                    Steam = new WindowsSteam();
                    break;

                case PlatformID.Unix:
                    Steam = new UnixSteam();
                    break;

                default:
                    throw new PlatformNotSupportedException();
            }
            if (Config.IsIgnoringSteam != true || CoreEnvironmentSettings.IsSteamCompatTool)
            {
                foreach (var app in apps)
                {
                    try
                    {
                        Steam.Initialize(app.Key);
                        Log.Information($"Successfully initialized Steam entry {app.Key} - {app.Value}");
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, $"Failed to initialize Steam Steam entry {app.Key} - {app.Value}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Steam couldn't load");
        }

        // Manual or auto injection setup.
        DalamudLoadInfo = new DalamudOverlayInfoProxy();
        DalamudUpdater = CreateDalamudUpdater();
        DalamudUpdater.Run();

        CreateCompatToolsInstance();

        Log.Debug("Creating Veldrid devices...");

#if DEBUG
        var version = AppUtil.GetGitHash();
#else
        var version = $"{AppUtil.GetAssemblyVersion()} ({AppUtil.GetGitHash()})";
#endif

        // Create window, GraphicsDevice, and all resources necessary for the demo.
        VeldridStartup.CreateWindowAndGraphicsDevice(
            new WindowCreateInfo(50, 50, 1280, 800, WindowState.Normal, $"XIVLauncherCN {version}"),
            new GraphicsDeviceOptions(false, null, true, ResourceBindingModel.Improved, true, true),
            out window,
            out gd);

        window.Resized += () =>
        {
            gd.MainSwapchain.Resize((uint)window.Width, (uint)window.Height);
            bindings.WindowResized(window.Width, window.Height);
            Invalidate();
        };
        cl = gd.ResourceFactory.CreateCommandList();
        Log.Debug("Veldrid OK!");

        bindings = new ImGuiBindings(gd, gd.MainSwapchain.Framebuffer.OutputDescription, window.Width, window.Height, storage.GetFile("launcherUI.ini"), Config.FontPxSize ?? 21.0f);
        Log.Debug("ImGui OK!");

        StyleModelV1.DalamudStandard.Apply();
        ImGui.GetIO().FontGlobalScale = Config.GlobalScale ?? 1.0f;

        var launcherClientConfig = LauncherClientConfig.GetAsync().GetAwaiter().GetResult();
        launcherApp = new LauncherApp(storage, launcherClientConfig.frontierUrl, launcherClientConfig.cutOffBootver);

        Invalidate(20);

        // Main application loop
        while (window.Exists)
        {
            Thread.Sleep(50);

            InputSnapshot snapshot = window.PumpEvents();

            if (!window.Exists)
                break;

            var overlayNeedsPresent = false;

            if (Steam != null && Steam.IsValid)
                overlayNeedsPresent = Steam.BOverlayNeedsPresent;

            if (!snapshot.KeyEvents.Any() && !snapshot.MouseEvents.Any() && !snapshot.KeyCharPresses.Any() && invalidationFrames == 0 && lastMousePosition == snapshot.MousePosition
                && !overlayNeedsPresent)
            {
                continue;
            }

            if (invalidationFrames == 0)
            {
                invalidationFrames = 10;
            }

            if (invalidationFrames > 0)
            {
                invalidationFrames--;
            }

            lastMousePosition = snapshot.MousePosition;

            bindings.Update(1f / 60f, snapshot);

            launcherApp.Draw();

            cl.Begin();
            cl.SetFramebuffer(gd.MainSwapchain.Framebuffer);
            cl.ClearColorTarget(0, new RgbaFloat(ClearColor.X, ClearColor.Y, ClearColor.Z, 1f));
            bindings.Render(gd, cl);
            cl.End();
            gd.SubmitCommands(cl);
            gd.SwapBuffers(gd.MainSwapchain);
        }

        // Don't dispose Veldrid resources — the SDL2 window surface was already
        // destroyed by the X button, and disposing the GraphicsDevice would crash.
        // The OS cleans up all GPU resources on process exit anyway.

        HttpClient.Dispose();

        if (Patcher is not null)
        {
            Patcher.CancelAllDownloads();
            Task.Run(async () =>
            {
                await PatchManager.UnInitializeAcquisition().ConfigureAwait(false);
                Environment.Exit(0);
            });
        }
    }

    public static void CreateCompatToolsInstance()
    {
        if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            return;

        // Only Linux is supported: Proton/umu does not run on macOS.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            throw new PlatformNotSupportedException("XIVLauncherCN 已移除 macOS Wine 支持，请使用 Windows 或 Linux 版本。");

        if (WineManager is null)
            throw new PlatformNotSupportedException("XIVLauncherCN 仅支持 Windows 与 Linux。");

        // ---- Proton/UMU mode (the only mode on Linux) ----
        var wineLogFile = new FileInfo(Path.Combine(storage.GetFolder("logs").FullName, "wine.log"));
        var winePrefix = !string.IsNullOrEmpty(CoreEnvironmentSettings.ProtonPrefix)
            ? new DirectoryInfo(CoreEnvironmentSettings.ProtonPrefix)
            : storage.GetFolder("protonprefix");
        var gamePath = Config.GamePath ?? storage.GetFolder("ffxiv");
        var gameConfigPath = Config.GameConfigPath ?? storage.GetFolder("ffxivConfig");

        var startupType = Config.RB_WineStartupType == RBWineStartupType.Custom
            ? RBWineStartupType.Custom
            : RBWineStartupType.Proton;

        // Determine proton version
        IWineRelease protonRelease;
        if (startupType == RBWineStartupType.Custom &&
            !string.IsNullOrEmpty(Config.RB_WineBinaryPath) &&
            WineSettings.IsValidProtonBinaryPath(Config.RB_WineBinaryPath))
        {
            // Custom Proton path
            var dir = new DirectoryInfo(Config.RB_WineBinaryPath);
            var name = dir.Name;
            protonRelease = new ProtonCustomRelease(name, $"Custom Proton at {Config.RB_WineBinaryPath}", name,
                dir.Parent?.FullName ?? "", "");
        }
        else
        {
            if (startupType == RBWineStartupType.Custom)
                Log.Error($"[PROTON] Custom path \"{Config.RB_WineBinaryPath}\" is not a valid Proton directory. Using the built-in GE-Proton instead.");

            // Only one built-in candidate exists: GE-Proton (downloaded on demand).
            protonRelease = WineManager.BuiltinProton;
        }

        // UMU setup - the umu launcher is always used to launch Proton.
        var useBuiltinUmu = CoreEnvironmentSettings.UseBuiltinUmu ||
                            Config.RB_UmuLauncher == RBUmuLauncherType.Builtin;
        WineManager.SetUmuLauncher(useBuiltinUmu);
        var umuRelease = WineManager.Runtime;
        var wineSync = Config.RB_WineSync ?? RBWineSyncType.FSync;

        var paths = new XLCorePaths(winePrefix, storage.Root, gamePath, gameConfigPath, WineManager.SteamFolder);
        var winSettings = new WineSettings(protonRelease, umuRelease, "", paths,
            Config.WineDebugVars ?? "-all", wineLogFile, wineSync, false);

        // Map old DxvkHudType values to the DXVK_HUD env var (same values)
        var hudType = Config.DxvkHudType;

        CompatibilityTools = new CompatibilityTools(
            winSettings,
            Config.RB_DxvkFrameRate ?? 0,
            hudType,
            Config.GameModeEnabled ?? false,
            Config.DxvkAsyncEnabled ?? true,
            false);
    }

    public static void ShowWindow()
    {
        window.Visible = true;
    }

    public static void HideWindow()
    {
        window.Visible = false;
    }
    
    

    public static void ClearSettings(bool tsbutton = false)
    {
        if (storage.GetFile("launcher.ini").Exists) storage.GetFile("launcher.ini").Delete();
        if (tsbutton)
        {
            LoadConfig(storage);
            launcherApp.State = LauncherApp.LauncherState.Settings;
        }
    }

    public static void ClearPrefix()
    {
        storage.GetFolder("wineprefix").Delete(true);
        storage.GetFolder("wineprefix");
        // Clear the proton prefix as well
        if (storage.GetFolder("protonprefix").Exists)
        {
            storage.GetFolder("protonprefix").Delete(true);
            storage.GetFolder("protonprefix");
        }
    }

    public static void ClearPlugins(bool tsbutton = false)
    {
        storage.GetFolder("dalamud").Delete(true);
        storage.GetFolder("dalamudAssets").Delete(true);
        storage.GetFolder("installedPlugins").Delete(true);
        storage.GetFolder("runtime").Delete(true);
        if (storage.GetFile("dalamudUI.ini").Exists) storage.GetFile("dalamudUI.ini").Delete();
        if (storage.GetFile("dalamudConfig.json").Exists) storage.GetFile("dalamudConfig.json").Delete();
        storage.GetFolder("dalamud");
        storage.GetFolder("dalamudAssets");
        storage.GetFolder("installedPlugins");
        storage.GetFolder("runtime");
        if (tsbutton)
        {
            DalamudLoadInfo = new DalamudOverlayInfoProxy();
            DalamudUpdater = CreateDalamudUpdater();
            DalamudUpdater.Run();
        }
    }

    public static void ClearTools(bool tsbutton = false)
    {
        storage.GetFolder("compatibilitytool").Delete(true);
        storage.GetFolder("compatibilitytool/umu");
        if (tsbutton) CreateCompatToolsInstance();
    }

    public static void ClearLogs(bool tsbutton = false)
    {
        storage.GetFolder("logs").Delete(true);
        storage.GetFolder("logs");
        string[] logfiles = { "dalamud.boot.log", "dalamud.boot.old.log", "dalamud.log", "dalamud.injector.log" };
        foreach (string logfile in logfiles)
            if (storage.GetFile(logfile).Exists) storage.GetFile(logfile).Delete();
        if (tsbutton)
            SetupLogging(mainArgs);

    }
    public static void ClearAll(bool tsbutton = false)
    {
        ClearSettings(tsbutton);
        ClearPrefix();
        ClearPlugins(tsbutton);
        ClearTools(tsbutton);
        ClearLogs(true);
    }

    public static void ResetUIDCache(bool tsbutton = false) => launcherApp.UniqueIdCache.Reset();
}
