using System;
using System.Numerics;
using System.Runtime.InteropServices;

using ImGuiNET;

using XIVLauncher.Common.Unix.Compatibility;
using XIVLauncher.Common.Unix.Compatibility.Wine;
using XIVLauncher.Common.Util;

namespace XIVLauncher.Core.Components.SettingsPage.Tabs;

public class SettingsTabWine : SettingsTab
{
    private readonly RunningModeEntry rbStartupTypeSetting;

    public SettingsTabWine()
    {
        rbStartupTypeSetting = new RunningModeEntry("Proton 来源",
            "选择 Proton 来源。内置 GE-Proton = 使用启动器自动下载管理的 GE-Proton（ProtonGE，推荐）；自定义 = 手动指定本机已有的 Proton 目录。",
            () => Program.Config.RB_WineStartupType ?? RBWineStartupType.Proton,
            x => Program.Config.RB_WineStartupType = x)
        {
            CheckValidity = mode =>
            {
                if (mode == RBWineStartupType.Custom &&
                    !XIVLauncher.Common.Unix.Compatibility.Wine.WineSettings.IsValidProtonBinaryPath(Program.Config.RB_WineBinaryPath))
                    return "Custom 模式需要有效的自定义 Proton 路径（包含 'proton' 可执行文件的目录）。";
                return null;
            }
        };

        Entries = new SettingsEntry[]
        {
            // ---- Running mode ----
            rbStartupTypeSetting,

            // Custom Proton path
            new SettingsEntry<string>("自定义 Proton 路径",
                "设置自定义 Proton 可执行文件所在目录的路径。\n该目录应包含 'proton' 可执行文件（例如 Steam 的 compatibilitytools.d 下的 Proton 目录）。",
                () => Program.Config.RB_WineBinaryPath ?? string.Empty,
                s => Program.Config.RB_WineBinaryPath = s)
            {
                CheckVisibility = () => Program.Config.RB_WineStartupType == RBWineStartupType.Custom,
                CheckValidity = path =>
                {
                    if (string.IsNullOrEmpty(path))
                        return "未设置路径。";
                    if (!XIVLauncher.Common.Unix.Compatibility.Wine.WineSettings.IsValidProtonBinaryPath(path))
                        return "该目录中没有找到 'proton' 可执行文件。";
                    return null;
                }
            },

            // UMU Launcher type - the umu launcher is mandatory for Proton.
            new UmuLauncherEntry("UMU Launcher",
                "Proton 通过 UMU（umu-launcher）启动，UMU 会提供类似 Steam 的运行环境（容器、前缀、挂载等），使 Proton 内置的 DXVK/NVAPI 正常工作。\nSystem = 优先使用系统安装的 umu-run，找不到时自动下载内置版本；Built-in = 使用内置下载的版本。"),

            // Sync type
            new SettingsEntry<RBWineSyncType>("同步类型",
                "选择 Wine/Proton 的同步原语。\nESync = eventfd（兼容性好）；FSync = futex2（推荐，需内核 5.16+）；NTSync = NT 同步（需内核 6.14+ 和 ntsync 模块）。",
                () => Program.Config.RB_WineSync ?? RBWineSyncType.FSync,
                x => Program.Config.RB_WineSync = x),

            // Frame rate limit (applies to the DXVK bundled with Proton)
            new NumericSettingsEntry("DXVK 帧率限制",
                "限制渲染帧率（作用于 Proton 内置 DXVK）。设为 0 表示无限制。",
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

            new SettingsEntry<DxvkHudType>("DXVK 覆盖层",
                "配置 DXVK（Proton 内置）覆盖层的显示内容。",
                () => Program.Config.DxvkHudType,
                type => Program.Config.DxvkHudType = type),

            new SettingsEntry<string>("WINEDEBUG 变量",
                "配置 Wine 的调试日志记录。有助于故障排除。",
                () => Program.Config.WineDebugVars ?? string.Empty,
                s => Program.Config.WineDebugVars = s)
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
/// Any legacy "Managed" config is mapped to Proton.
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

        ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
        ImGuiHelpers.TextWrapped(this.Description);
        ImGui.PopStyleColor();

        DrawDescriptionAndValidity();
    }

    private static string GetDisplayName(RBWineStartupType type)
    {
        return type switch
        {
            RBWineStartupType.Proton => "内置 GE-Proton（推荐）",
            RBWineStartupType.Custom => "自定义 Proton 路径",
            _ => type.ToString()
        };
    }
}

/// <summary>
/// UMU Launcher selection. Only System and Built-in are offered; a legacy
/// "Disabled" value is treated as System because Proton must always be
/// launched through the umu launcher.
/// </summary>
internal class UmuLauncherEntry : SettingsEntry<RBUmuLauncherType>
{
    private static readonly RBUmuLauncherType[] VisibleValues = { RBUmuLauncherType.System, RBUmuLauncherType.Builtin };

    public UmuLauncherEntry(string name, string description)
        : base(name, description,
            () =>
            {
                var val = Program.Config.RB_UmuLauncher ?? RBUmuLauncherType.System;
                return val == RBUmuLauncherType.Disabled ? RBUmuLauncherType.System : val;
            },
            x => Program.Config.RB_UmuLauncher = x)
    {
    }

    public override void Draw()
    {
        ImGuiHelpers.TextWrapped(this.Name);

        var current = this.Value;
        if (current == RBUmuLauncherType.Disabled)
            current = RBUmuLauncherType.System;

        var currentIdx = Array.IndexOf(VisibleValues, current);
        if (currentIdx < 0)
            currentIdx = 0;

        if (ImGui.BeginCombo($"###{Id}", GetDisplayName(current)))
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

        ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
        ImGuiHelpers.TextWrapped(this.Description);
        ImGui.PopStyleColor();

        DrawDescriptionAndValidity();
    }

    private static string GetDisplayName(RBUmuLauncherType type)
    {
        return type switch
        {
            RBUmuLauncherType.System => "System（系统 umu-run，自动兜底内置）",
            RBUmuLauncherType.Builtin => "Built-in（内置下载）",
            _ => type.ToString()
        };
    }
}
