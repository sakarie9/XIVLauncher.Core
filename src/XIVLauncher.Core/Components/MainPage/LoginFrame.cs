using System.Numerics;

using Castle.Core.Internal;

using ImGuiNET;

using XIVLauncher.Common.Game;
using XIVLauncher.Core.Accounts.Cred.CredProviders;
using XIVLauncher.Core.Accounts.Secrets.Providers;
using XIVLauncher.Core.Components.Common;

namespace XIVLauncher.Core.Components.MainPage;

public class LoginFrame : Component
{
    private readonly MainPage mainPage;

    private readonly Input loginInput;
    private readonly Combo areaCombo;
    private readonly Combo loginTypeCombo;
    private bool showPasswordInput = false;
    private readonly Input passwordInput;
    // private readonly Checkbox oneTimePasswordCheckbox;
    // private readonly Checkbox useSteamServiceCheckbox;
    private readonly Checkbox fastLoginCheckbox;
    private readonly Button loginButton;
    public SdoArea[] SdoAreas = Array.Empty<SdoArea>();

    public string Username
    {
        get => this.loginInput.Value;
        set => this.loginInput.Value = value;
    }
    public string Password
    {
        get => this.passwordInput.Value;
        set => this.passwordInput.Value = value;
    }

    public SdoArea? Area => SdoAreas.FirstOrDefault(area => area.AreaName == this.areaCombo.Value);
    public LoginType LoginType
    {
        get
        {
            var selectedIndex = Array.IndexOf(this.loginTypeCombo.Items, this.loginTypeCombo.Value);
            return (LoginType)Enum.GetValues<LoginType>().GetValue(selectedIndex);
        }
    }


    public bool IsOtp
    {
        get => false;
        set
        {
        }
    }

    public bool IsSteam
    {
        get => false;
        set
        {
        }
    }

    public bool IsAutoLogin
    {
        get => this.fastLoginCheckbox.Value;
        set => this.fastLoginCheckbox.Value = value;
    }

    public event Action<LoginAction>? OnLogin;

    private const string POPUP_ID_LOGINACTION = "popup_loginaction";

    public LoginFrame(MainPage mainPage)
    {
        this.mainPage = mainPage;
        this.areaCombo = new Combo("大区", SdoAreas.Select(area => area.AreaName).ToArray(), defaultItem: Program.Config.SelectedServer ?? 0, onSelectChange:
                                   (selectedServer) =>
                                   {
                                       Program.Config.SelectedServer = selectedServer;
                                   });
        this.loginInput = new Input("账号", "请输入账号", new Vector2(12f, 0f), 128);
        this.passwordInput = new Input("密码", "请输入密码", new Vector2(12f, 0f), 1280, flags: ImGuiInputTextFlags.Password | ImGuiInputTextFlags.NoUndoRedo);

        // this.loginInput = new Input("Username", "Enter your Username", new Vector2(12f, 0f), 128);
        // this.passwordInput = new Input("Password", "Enter your password", new Vector2(12f, 0f), 128, flags: ImGuiInputTextFlags.Password | ImGuiInputTextFlags.NoUndoRedo);

        // this.oneTimePasswordCheckbox = new Checkbox("Use one-time password");

        // this.useSteamServiceCheckbox = new Checkbox("Use steam service");
        this.loginTypeCombo = new Combo("登录方式", Enum.GetNames(typeof(LoginType)).Where( // AutoLogin is not an initial login type
                                            type => type != "AutoLoginSession").Select(
                                            loginType => loginType switch
        {
            "SdoStatic" => "密码登录",
            "SdoSlide" => "滑动登陆",
            "SdoQrCode" => "扫码登录",
            "WeGameToken" => "Wegame Token 登录",
            "WeGameSid" => "Wegame SID 登录",
            // "AutoLoginSession" => "自动登录"
        }).ToArray(), defaultItem: Program.Config.SelectedLoginType ?? 2, onSelectChange: 
                                        (selectedLoginType) =>
                                        {
                                            Program.Config.SelectedLoginType = selectedLoginType;
                                        });

        this.fastLoginCheckbox = new Checkbox("快速登陆");

        this.loginButton = new Button("登陆");
        this.loginButton.Click += () => { this.OnLogin?.Invoke(LoginAction.Game); };

        this.ReloadAreas();
    }

    public void ReloadAreas()
    {
        Task.Run(async () =>
        {
            try
            {
                SdoAreas = await SdoArea.Get();
            }
            catch (Exception ex)
            {
                SdoAreas = new SdoArea[1] { new SdoArea { AreaName = "服务器状态异常", Areaid = "-1" } };
                throw ex;
            }
            finally
            {
                this.areaCombo.Items = SdoAreas.Select(area => area.AreaName).ToArray();
            }
        });
    }

    private Vector2 GetSize()
    {
        var vp = ImGuiHelpers.ViewportSize;
        return new Vector2(-1, vp.Y - 128f);
    }

    private void TogglePasswordInput()
    {
        this.showPasswordInput = !this.showPasswordInput;
    }

    public override void Draw()
    {
        if (ImGui.BeginChild("###loginFrame", this.GetSize()))
        {
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(32f, 32f));
            this.areaCombo.Draw();
            this.loginInput.Draw();
            if (this.showPasswordInput)
            {
                this.passwordInput.Draw();
            }

            this.loginTypeCombo.Draw();
            this.fastLoginCheckbox.Draw();

            ImGui.Dummy(new Vector2(10));

            this.loginButton.Draw();

            ImGui.PopStyleVar();

            ImGui.NewLine();

            ImGui.OpenPopupOnItemClick(POPUP_ID_LOGINACTION, ImGuiPopupFlags.MouseButtonRight);

            ImGui.PushStyleColor(ImGuiCol.PopupBg, ImGuiColors.BlueShade1);

            if (ImGui.BeginPopupContextItem(POPUP_ID_LOGINACTION))
            {
                if (ImGui.MenuItem("启动但不加载 Dalamud"))
                {
                    this.OnLogin?.Invoke(LoginAction.GameNoDalamud);
                }

                ImGui.Separator();

                if (ImGui.MenuItem("启动但不加载任何插件"))
                {
                    this.OnLogin?.Invoke(LoginAction.GameNoPlugins);
                }

                ImGui.Separator();

                if (ImGui.MenuItem("启动但不加载自定义插件"))
                {
                    this.OnLogin?.Invoke(LoginAction.GameNoThirdparty);
                }

                ImGui.Separator();

                if (ImGui.MenuItem("更新游戏不启动"))
                {
                    this.OnLogin?.Invoke(LoginAction.GameNoLaunch);
                }
                ImGui.Separator();

                if (ImGui.MenuItem("修复游戏"))
                {
                    this.OnLogin?.Invoke(LoginAction.Repair);
                }

                ImGui.Separator();

                if (ImGui.MenuItem("扫码登录"))
                {
                    this.OnLogin?.Invoke(LoginAction.ForceQR);
                }

                ImGui.Separator();

                if (ImGui.MenuItem("显示/隐藏密码框"))
                {
                    this.TogglePasswordInput();
                }

                if (LauncherApp.IsDebug)
                {
                    ImGui.Separator();

                    if (ImGui.MenuItem("Fake Login"))
                    {
                        this.OnLogin?.Invoke(LoginAction.Fake);
                    }
                }

                ImGui.EndPopup();
            }

            ImGui.PopStyleColor();

            if (Program.AccountManager.CredProvider is NoCred)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudRed);
                ImGui.TextWrapped("注意！未配置任何凭据提供程序。请注意密码安全。");
                ImGui.PopStyleColor();

                ImGui.Dummy(new Vector2(15));
            }

            ImGui.PushFont(FontManager.IconFont);

            var extraButtonSize = new Vector2(45) * ImGuiHelpers.GlobalScale;

            if (ImGui.Button(FontAwesomeIcon.CaretDown.ToIconString(), extraButtonSize))
            {
                ImGui.OpenPopup(POPUP_ID_LOGINACTION);
            }

            ImGui.SameLine();

            if (ImGui.Button(FontAwesomeIcon.UserFriends.ToIconString(), extraButtonSize))
            {
                this.mainPage.AccountSwitcher.Open();
            }

            ImGui.PopFont();
        }

        ImGui.EndChild();

        base.Draw();
    }
}
