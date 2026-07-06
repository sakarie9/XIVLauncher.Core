using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;

using ImGuiNET;

using XIVLauncher.Common.Unix.Compatibility;
using XIVLauncher.Common.Unix.Compatibility.Dxvk;
using XIVLauncher.Common.Unix.Compatibility.Nvapi;
using XIVLauncher.Common.Unix.Compatibility.Wine;
using XIVLauncher.Common.Util;

namespace XIVLauncher.Core.Components.SettingsPage.Tabs;

public class SettingsTabWine : SettingsTab
{
    private readonly RunningModeEntry rbStartupTypeSetting;

    public SettingsTabWine()
    {
        rbStartupTypeSetting = new RunningModeEntry("运行模式",
            "选择 Proton 管理模式。Proton = 自动管理的 Proton 版本；Custom = 手动指定 Wine 或 Proton 路径",
            () => Program.Config.RB_WineStartupType ?? RBWineStartupType.Proton,
            x => Program.Config.RB_WineStartupType = x);

        Entries = new SettingsEntry[]
        {
            // ---- Running mode ----
            rbStartupTypeSetting,

            // Proton version selector
            new SettingsEntry<string>("Proton 版本",
                "选择要使用的 Proton 版本。默认使用 RB 的 proton-xiv（对 FF14 有优化）。\n你也可以选择已安装的 Steam Proton 或自定义 Proton。",
                () => Program.Config.RB_ProtonVersion ?? Program.WineManager.DEFAULTPROTON,
                s => Program.Config.RB_ProtonVersion = s)
            {
                CheckVisibility = () => Program.Config.RB_WineStartupType == RBWineStartupType.Proton
            },

            // Custom Wine/Proton binary path
            new SettingsEntry<string>("自定义 Wine/Proton 路径",
                "设置自定义 Wine 或 Proton 二进制文件的路径。\n对于 Wine：指向包含 wine64 的目录。\n对于 Proton：指向包含 'proton' 可执行文件的目录。",
                () => Program.Config.RB_WineBinaryPath ?? "/usr/bin",
                s => Program.Config.RB_WineBinaryPath = s)
            {
                CheckVisibility = () => Program.Config.RB_WineStartupType == RBWineStartupType.Custom
            },

            // UMU Launcher type
            new SettingsEntry<RBUmuLauncherType>("UMU Launcher",
                "UMU 是 Proton 的运行环境管理器。\nSystem = 优先使用系统安装的 umu-run；Built-in = 使用内置版本；Disabled = 不使用 UMU。",
                () => Program.Config.RB_UmuLauncher ?? RBUmuLauncherType.System,
                x => Program.Config.RB_UmuLauncher = x)
            {
                CheckVisibility = () => Program.Config.RB_WineStartupType == RBWineStartupType.Proton
            },

            // Sync type
            new SettingsEntry<RBWineSyncType>("同步类型",
                "选择 Wine/Proton 的同步原语。\nESync = eventfd（兼容性好）；FSync = futex2（推荐，需内核 5.16+）；NTSync = NT 同步（需内核 6.14+ 和 ntsync 模块）。",
                () => Program.Config.RB_WineSync ?? RBWineSyncType.FSync,
                x => Program.Config.RB_WineSync = x),

            // DXVK version selector
            new DxvkVersionEntry("DXVK 版本",
                "选择 DXVK 版本。一般选择版本号最大的即可。GPLAsync 为含异步补丁的版本。\n选择「禁用」则使用 Proton 内置的 DXVK。"),

            // DXVK custom path
            new SettingsEntry<string>("DXVK 自定义路径",
                "设置自定义 DXVK 目录的路径。\n该目录应包含 x64 子目录（内含 d3d11.dll 等文件）。\n仅当上方 DXVK 版本选择为「自定义路径」时生效。",
                () => Program.Config.RB_DxvkCustomPath ?? string.Empty,
                s => Program.Config.RB_DxvkCustomPath = s)
            {
                CheckVisibility = () => Program.DxvkManager.GetVersionOrDefault(Program.Config.RB_DxvkVersion) == DxvkManager.CUSTOM_PATH_NAME
            },

            // Nvapi (DLSS) version selector
            new NvapiVersionEntry("Nvapi（DLSS）版本",
                "选择 dxvk-nvapi 版本以启用 DLSS 支持。\n首次启动时会自动查找系统中的 nvngx.dll 并创建符号链接。\n如果使用 AMD 或 Intel 显卡，选择「禁用」。"),

            // Nvapi custom path
            new SettingsEntry<string>("Nvapi 自定义路径",
                "设置自定义 dxvk-nvapi 目录的路径。\n该目录应包含 x64 子目录（内含 nvapi64.dll 等文件）。\n仅当上方 Nvapi 版本选择为「自定义路径」时生效。",
                () => Program.Config.RB_NvapiCustomPath ?? string.Empty,
                s => Program.Config.RB_NvapiCustomPath = s)
            {
                CheckVisibility = () =>
                {
                    var ver = !string.IsNullOrEmpty(Program.Config.RB_NvapiVersion)
                        ? Program.NvapiManager.GetVersionOrDefault(Program.Config.RB_NvapiVersion)
                        : (Program.Config.RB_NvapiEnabled == true ? null : "DISABLED");
                    return ver == NvapiManager.CUSTOM_PATH_NAME;
                }
            },

            // Frame rate limit
            new NumericSettingsEntry("DXVK 帧率限制",
                "限制渲染帧率。设为 0 表示无限制。",
                () => Program.Config.RB_DxvkFrameRate ?? 0,
                fps => Program.Config.RB_DxvkFrameRate = fps,
                0, int.MaxValue, 1),

#if !FLATPAK
            new SettingsEntry<bool>("启用 Feral GameMode",
                "使用 Feral Interactive 的 GameMode CPU 优化来启动游戏。",
                () => Program.Config.GameModeEnabled ?? true,
                b => Program.Config.GameModeEnabled = b)
            {
                CheckVisibility = () => RuntimeInformation.IsOSPlatform(OSPlatform.Linux),
                CheckValidity = b =>
                {
                    var handle = IntPtr.Zero;
                    if (b == true && !NativeLibrary.TryLoad("libgamemodeauto.so.0", out handle))
                        return "GameMode 未在系统中检测到。";
                    NativeLibrary.Free(handle);
                    return null;
                }
            },
#endif

            new SettingsEntry<bool>("设置 Windows 版本为 7",
                "Wine 8.1+ 的默认值是 Windows 10，但某些 Dalamud 插件会导致问题。建议使用 Windows 7。",
                () => Program.Config.SetWin7 ?? true,
                b => Program.Config.SetWin7 = b),

            new SettingsEntry<DxvkHudType>("DXVK 覆盖层",
                "配置 DXVK 覆盖层的显示内容。",
                () => Program.Config.DxvkHudType,
                type => Program.Config.DxvkHudType = type),

            new SettingsEntry<string>("WINEDEBUG 变量",
                "配置 Wine 的调试日志记录。有助于故障排除。",
                () => Program.Config.WineDebugVars ?? string.Empty,
                s => Program.Config.WineDebugVars = s),

            new SettingsEntry<string>("WINE ENV",
                "为 Wine 配置环境变量。",
                () => Program.Config.WineEnv ?? string.Empty,
                s => Program.Config.WineEnv = s)
        };
    }

    public override SettingsEntry[] Entries { get; }

    public override bool IsUnixExclusive => true;

    public override string Title => "Wine";

    public override void Draw()
    {
        base.Draw();

        if (!Program.CompatibilityTools.IsToolDownloaded)
        {
            ImGui.BeginDisabled();
            ImGui.Text("兼容性工具尚未设置。请至少启动一次游戏。");

            ImGui.Dummy(new Vector2(10));
        }

        if (ImGui.Button("打开前缀"))
        {
            var prefix = Program.CompatibilityTools.IsToolReady
                ? Program.CompatibilityTools.Prefix.FullName
                : Program.storage.GetFolder("protonprefix").FullName;
            PlatformHelpers.OpenBrowser(prefix);
        }

        ImGui.SameLine();

        if (ImGui.Button("打开 Wine 配置"))
        {
            Program.CompatibilityTools.RunInPrefix("winecfg");
        }

        ImGui.SameLine();

        if (ImGui.Button("打开 Wine 资源管理器"))
        {
            Program.CompatibilityTools.RunInPrefix("explorer");
        }

        if (ImGui.Button("终止所有 Wine 进程"))
        {
            Program.CompatibilityTools.Kill();
        }

        if (!Program.CompatibilityTools.IsToolDownloaded)
        {
            ImGui.EndDisabled();
        }
    }

    public override void Save()
    {
        base.Save();
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Program.CreateCompatToolsInstance();
    }
}

/// <summary>
/// Settings entry for RBWineStartupType that only shows Proton and Custom options.
/// "Managed" mode is removed from the UI; any existing Managed config is mapped to Proton.
/// </summary>
internal class RunningModeEntry : SettingsEntry<RBWineStartupType>
{
    private static readonly RBWineStartupType[] VisibleValues = { RBWineStartupType.Proton, RBWineStartupType.Custom };

    public RunningModeEntry(string name, string description, Func<RBWineStartupType> load, Action<RBWineStartupType> save)
        : base(name, description, load, save)
    {
    }

    public override void Draw()
    {
        ImGuiHelpers.TextWrapped(this.Name);

        // Map Managed → Proton for backward compat, then map to visible array index
        var rawVal = (RBWineStartupType)(this.InternalValue ?? 1);
        if (rawVal == RBWineStartupType.Managed)
            rawVal = RBWineStartupType.Proton;

        var currentIdx = Array.IndexOf(VisibleValues, rawVal);
        if (currentIdx < 0)
            currentIdx = 0;

        if (ImGui.BeginCombo($"###{Id}", GetDisplayName(rawVal)))
        {
            for (var i = 0; i < VisibleValues.Length; i++)
            {
                var val = VisibleValues[i];
                if (ImGui.Selectable(GetDisplayName(val), currentIdx == i))
                {
                    this.InternalValue = (int)val;
                }
            }

            ImGui.EndCombo();
        }

        // Description
        ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
        ImGuiHelpers.TextWrapped(this.Description);
        ImGui.PopStyleColor();

        // Validity check
        if (this.CheckValidity != null)
        {
            var validityMsg = this.CheckValidity.Invoke(this.Value);
            this.IsValid = string.IsNullOrEmpty(validityMsg);

            if (!this.IsValid)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudRed);
                ImGui.Text(validityMsg);
                ImGui.PopStyleColor();
            }
        }
        else
        {
            this.IsValid = true;
        }

        // Warning
        var warningMessage = this.CheckWarning?.Invoke(this.Value);

        if (warningMessage != null)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudRed);
            ImGui.Text(warningMessage);
            ImGui.PopStyleColor();
        }
    }

    private static string GetDisplayName(RBWineStartupType type)
    {
        return type switch
        {
            RBWineStartupType.Proton => "Proton（推荐）",
            RBWineStartupType.Custom => "Custom 自定义",
            _ => type.ToString()
        };
    }
}

/// <summary>
/// Dropdown entry for DXVK version selection. Shows available DXVK versions
/// from DxvkManager.Version as a combo box instead of a free-text input.
/// </summary>
internal class DxvkVersionEntry : SettingsEntry<string>
{
    private string[] _cachedKeys = Array.Empty<string>();

    public DxvkVersionEntry(string name, string description)
        : base(name, description,
            () => Program.Config.RB_DxvkVersion ?? Program.DxvkManager.DEFAULT,
            s => Program.Config.RB_DxvkVersion = s)
    {
    }

    public override void Draw()
    {
        ImGuiHelpers.TextWrapped(this.Name);

        var versions = Program.DxvkManager.Version;
        var keys = versions.Keys.ToArray();

        // Rebuild display items when the version list changes
        _cachedKeys = keys;

        var current = this.Value ?? Program.DxvkManager.DEFAULT;

        // If current value is not in the list, fall back to default
        if (!versions.ContainsKey(current))
            current = Program.DxvkManager.DEFAULT;

        if (ImGui.BeginCombo($"###{Id}", GetDisplayName(current, versions)))
        {
            foreach (var key in _cachedKeys)
            {
                if (ImGui.Selectable(GetDisplayName(key, versions), key == current))
                {
                    this.InternalValue = key;
                }
            }

            ImGui.EndCombo();
        }

        // Description
        ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
        ImGuiHelpers.TextWrapped(this.Description);
        ImGui.PopStyleColor();

        // Validity
        if (this.CheckValidity != null)
        {
            var validityMsg = this.CheckValidity.Invoke(this.Value);
            this.IsValid = string.IsNullOrEmpty(validityMsg);

            if (!this.IsValid)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudRed);
                ImGui.Text(validityMsg);
                ImGui.PopStyleColor();
            }
        }
        else
        {
            this.IsValid = true;
        }

        var warningMessage = this.CheckWarning?.Invoke(this.Value);

        if (warningMessage != null)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudRed);
            ImGui.Text(warningMessage);
            ImGui.PopStyleColor();
        }
    }

    private static string GetDisplayName(string key, IReadOnlyDictionary<string, IToolRelease> versions)
    {
        if (key == "DISABLED")
            return "禁用（使用 Proton 内置 DXVK）";

        if (versions.TryGetValue(key, out var release))
        {
            if (!string.IsNullOrEmpty(release.Label))
                return release.Label;
        }

        return key;
    }
}

/// <summary>
/// Dropdown entry for Nvapi version selection. Shows available Nvapi versions
/// from NvapiManager.Version as a combo box instead of a free-text input.
/// </summary>
internal class NvapiVersionEntry : SettingsEntry<string>
{
    private string[] _cachedKeys = Array.Empty<string>();

    public NvapiVersionEntry(string name, string description)
        : base(name, description,
            () => !string.IsNullOrEmpty(Program.Config.RB_NvapiVersion)
                ? Program.Config.RB_NvapiVersion
                : (Program.Config.RB_NvapiEnabled == true ? Program.NvapiManager.DEFAULT : "DISABLED"),
            s => Program.Config.RB_NvapiVersion = s)
    {
    }

    public override void Draw()
    {
        ImGuiHelpers.TextWrapped(this.Name);

        var versions = Program.NvapiManager.Version;
        var keys = versions.Keys.ToArray();

        _cachedKeys = keys;

        var current = this.Value ?? Program.NvapiManager.DEFAULT;

        if (!versions.ContainsKey(current))
            current = Program.NvapiManager.DEFAULT;

        if (ImGui.BeginCombo($"###{Id}", NvapiGetDisplayName(current, versions)))
        {
            foreach (var key in _cachedKeys)
            {
                if (ImGui.Selectable(NvapiGetDisplayName(key, versions), key == current))
                {
                    this.InternalValue = key;
                }
            }

            ImGui.EndCombo();
        }

        ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
        ImGuiHelpers.TextWrapped(this.Description);
        ImGui.PopStyleColor();

        if (this.CheckValidity != null)
        {
            var validityMsg = this.CheckValidity.Invoke(this.Value);
            this.IsValid = string.IsNullOrEmpty(validityMsg);

            if (!this.IsValid)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudRed);
                ImGui.Text(validityMsg);
                ImGui.PopStyleColor();
            }
        }
        else
        {
            this.IsValid = true;
        }

        var warningMessage = this.CheckWarning?.Invoke(this.Value);

        if (warningMessage != null)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudRed);
            ImGui.Text(warningMessage);
            ImGui.PopStyleColor();
        }
    }

    private static string NvapiGetDisplayName(string key, IReadOnlyDictionary<string, IToolRelease> versions)
    {
        if (key == "DISABLED")
            return "禁用（不使用 Nvapi）";

        if (versions.TryGetValue(key, out var release))
        {
            if (!string.IsNullOrEmpty(release.Label))
                return release.Label;
        }

        return key;
    }
}
