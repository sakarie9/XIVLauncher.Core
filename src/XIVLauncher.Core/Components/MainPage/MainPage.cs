using System.Diagnostics;
using System.Numerics;

using Castle.Core.Internal;

using CheapLoc;

using ImGuiNET;

using Serilog;

using XIVLauncher.Common;
using XIVLauncher.Common.Addon;
using XIVLauncher.Common.Dalamud;
using XIVLauncher.Common.Game;
using XIVLauncher.Common.Game.Exceptions;
using XIVLauncher.Common.Game.Patch;
using XIVLauncher.Common.Game.Patch.Acquisition;
using XIVLauncher.Common.Game.Patch.PatchList;
using XIVLauncher.Common.PlatformAbstractions;
using XIVLauncher.Common.Unix;
using XIVLauncher.Common.Unix.Compatibility;
using XIVLauncher.Common.Unix.Compatibility.GameFixes;
using XIVLauncher.Common.Util;
using XIVLauncher.Common.Windows;
using XIVLauncher.Core.Accounts;
using XIVLauncher.Core.Support;

namespace XIVLauncher.Core.Components.MainPage;

public class MainPage : Page
{
    private readonly LoginFrame loginFrame;
    private readonly NewsFrame newsFrame;
    private readonly ActionButtons actionButtons;
    public SdoArea? Area;

    public bool IsLoggingIn { get; private set; }
    

    private string _loginMessage = "正在登录...";
    public string LoginMessage
    {
        get => _loginMessage;
        set
        {
            _loginMessage = value;
        }
    }
    private CancellationTokenSource loginCts;
    public void CancelLogin()
    {
        if (this.loginCts != null)
        {
            Log.Information("取消登陆");
            this.loginCts.Cancel();
        }
    }
    

    public MainPage(LauncherApp app)
        : base(app)
    {
        this.loginFrame = new LoginFrame(this);
        this.newsFrame = new NewsFrame(app);

        this.actionButtons = new ActionButtons();

        this.AccountSwitcher = new AccountSwitcher(app.Accounts);
        this.AccountSwitcher.AccountChanged += this.AccountSwitcherOnAccountChanged;

        this.loginFrame.OnLogin += this.ProcessLogin;
        this.actionButtons.OnSettingsButtonClicked += () => this.App.State = LauncherApp.LauncherState.Settings;
        this.actionButtons.OnStatusButtonClicked += () => AppUtil.OpenBrowser("https://is.xivup.com/");

        this.Padding = new Vector2(32f, 32f);

        var savedAccount = App.Accounts.CurrentAccount;

        if (savedAccount != null) this.SwitchAccount(savedAccount, false);

        if (PlatformHelpers.IsElevated())
            App.ShowMessage("XIVLauncherCN is running as administrator/root user.\nThis can cause various issues, including but not limited to addons failing to launch and hotkey applications failing to respond.\n\nPlease take care to avoid running XIVLauncher with elevated privileges", "XIVLauncherCN");

        Troubleshooting.LogTroubleshooting();
    }

    public AccountSwitcher AccountSwitcher { get; private set; }

    public void DoAutoLoginIfApplicable()
    {
        Debug.Assert(App.State == LauncherApp.LauncherState.Main);

        if ((App.Settings.IsAutologin ?? false) && !string.IsNullOrEmpty(this.loginFrame.Username) && !string.IsNullOrEmpty(this.loginFrame.Password))
            ProcessLogin(LoginAction.Game);
    }

    public override void Draw()
    {
        base.Draw();

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(32f, 32f));
        this.newsFrame.Draw();

        ImGui.SameLine();

        this.AccountSwitcher.Draw();
        this.loginFrame.Draw();

        this.actionButtons.Draw();
    }

    public void ReloadNews() => this.newsFrame.ReloadNews();

    private void SwitchAccount(XivAccount account, bool saveAsCurrent)
    {
        this.loginFrame.Username = account.UserName;
        this.loginFrame.IsAutoLogin = App.Settings.IsAutologin ?? false;

        if (!account.Password.IsNullOrEmpty())
            this.loginFrame.Password = account.Password;

        if (saveAsCurrent)
        {
            App.Accounts.CurrentAccount = account;
        }
    }

    private void AccountSwitcherOnAccountChanged(object? sender, XivAccount e)
    {
        SwitchAccount(e, true);
    }

    private void ProcessLogin(LoginAction action)
    {
        if (this.IsLoggingIn)
            return;

        this.App.StartLoading("正在登录...", canDisableAutoLogin: false);

        // if (Program.UsesFallbackSteamAppId && this.loginFrame.IsSteam)
        //     throw new Exception("Doesn't own Steam AppId on this account.");

        this.loginCts = new CancellationTokenSource();

        Task.Run(async () =>
        {
            if (GameHelpers.CheckIsGameOpen() && action == LoginAction.Repair)
            {
                App.ShowMessageBlocking("The game and/or the official launcher are open. XIVLauncherCN cannot repair the game if this is the case.\nPlease close them and try again.", "XIVLauncherCN");

                Reactivate();
                return;
            }

            if (Repository.Ffxiv.GetVer(App.Settings.GamePath) == Constants.BASE_GAME_VERSION &&
                App.Settings.IsUidCacheEnabled == true)
            {
                App.ShowMessageBlocking(
                    "You enabled the UID cache in the patcher settings.\nThis setting does not allow you to reinstall FFXIV.\n\nIf you want to reinstall FFXIV, please take care to disable it first.",
                    "XIVLauncherCN Error");

                this.Reactivate();
                return;
            }

            IsLoggingIn = true;

            App.Settings.IsAutologin = this.loginFrame.IsAutoLogin;
            var loginType = this.loginFrame.LoginType;

            var result = await Login(
                             loginType,
                             this.loginFrame.Username,
                             this.loginFrame.Password,
                             this.loginFrame.IsAutoLogin,
                    loginType == LoginType.WeGameSid || loginType == LoginType.WeGameToken,
                             action).ConfigureAwait(false);

            if (result)
            {
                Environment.Exit(0);
            }
            else
            {
                Log.Verbose("Reactivated after Login() != true");
                this.Reactivate();
            }
        }).ContinueWith(t =>
        {
            if (!App.HandleContinuationBlocking(t))
                this.Reactivate();
        });
    }

    public async Task<bool> Login(LoginType loginType, string username, string password, bool doingAutoLogin, bool isWegame, LoginAction action)
    {
        if (action == LoginAction.Fake)
        {
            IGameRunner gameRunner;
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                gameRunner = new WindowsGameRunner(null, false, Program.DalamudUpdater.Runtime);
            else
                gameRunner = new UnixGameRunner(Program.CompatibilityTools, null, false);

            var launchedProcess = App.Launcher.LaunchGameSdo(
                gameRunner,
                "0",
                "0",
                "1",
                "2",
                "",
                "",
                "",
                App.Settings.GamePath!,
                false,
                DpiAwareness.Unaware
            );

            return false;
        }

        var bootRes = await HandleBootCheck().ConfigureAwait(false);

        if (!bootRes)
            return false;
/*
        if (Area == null || Area.Areaid == "-1")
        {
            App.ShowMessageBlocking(
                "未能获取到服务器列表,无法登陆",
                "XIVLauncherCN Error");
            return false;
        }
*/
        var finalLoginType = loginType;
        var serect = string.Empty;
        
        var accountType = loginType switch
        {
            LoginType.WeGameSid => XivAccountType.WeGameSid,
            LoginType.WeGameToken => XivAccountType.WeGame,
            _ => XivAccountType.Sdo
        };
        
        
        try
        {
            var savedAccount = this.App.Accounts.Accounts.FirstOrDefault(x => x.UserName == username && x.AccountType == accountType);
            switch (loginType)
            {
                case LoginType.SdoStatic:
                    if (!password.IsNullOrEmpty())
                    {
                        serect = password;
                    }
                    else if (savedAccount != null)
                    {
                        serect = await this.App.Accounts.Decrypt(savedAccount.Password).ConfigureAwait(false);
                    }
                    ArgumentException.ThrowIfNullOrEmpty(username, "静态登录用户名");
                    ArgumentException.ThrowIfNullOrEmpty(serect, "静态登录密码");
                    finalLoginType = LoginType.SdoStatic;
                    break;
                case LoginType.WeGameSid:
                    doingAutoLogin = true;
                    if (savedAccount != null)
                    {
                        serect = await this.App.Accounts.Decrypt(savedAccount.TestSID).ConfigureAwait(false);
                    }
                    else
                    {
                        throw new Exception("获取WeGame登录信息失败");
                    }
                    finalLoginType = LoginType.WeGameSid;
                    break;
                case LoginType.WeGameToken:
                    if (password.IsNullOrEmpty())
                    {
                        serect = await this.App.Accounts.CredProvider.Decrypt(savedAccount.AutoLoginSessionKey);
                        finalLoginType = LoginType.AutoLoginSession;
                    }
                    if (serect.IsNullOrEmpty())
                    {
                        serect = password;
                        finalLoginType = LoginType.WeGameToken;
                    }
                    ArgumentException.ThrowIfNullOrEmpty(serect, "自动登录密钥或者Token");
                    break;
                case LoginType.SdoSlide:
                    if (savedAccount != null && doingAutoLogin)
                    {
                        serect = await this.App.Accounts.Decrypt(savedAccount.AutoLoginSessionKey);
                        finalLoginType = LoginType.AutoLoginSession;
                    }
                    if (serect.IsNullOrEmpty())
                    {
                        finalLoginType = LoginType.SdoSlide;
                    }
                    ArgumentException.ThrowIfNullOrEmpty(username, "一键滑动登录用户名");
                    break;
                case LoginType.SdoQrCode:
                    break;
            }
        }
        catch (Exception ex)
        {
            if (ex is ArgumentException argEx)
            {
                Log.Error(ex, "Failed to encrypt text");
                App.ShowMessageBlocking($"{argEx.ParamName}不能为空", "XIVLauncherCN Error");
                return false;
            }
            throw;
        }
        var loginResult = await TryLoginToGame(finalLoginType, loginType, username, serect, doingAutoLogin, action).ConfigureAwait(false);

        if (loginResult == null)
            return false;

        loginResult.Area = Area;
        if (loginResult.State == Launcher.LoginState.NeedsPatchGame && action != LoginAction.Repair)
        {
            action = LoginAction.GameNoLaunch;
        }
        if (action != LoginAction.GameNoLaunch)
        {
            if (loginResult.State == Launcher.LoginState.Ok)
            {
                var accountToSave = new XivAccount()
                {
                    AutoLogin = loginType == LoginType.WeGameSid || doingAutoLogin,
                    LoginAccount = loginResult.OauthLogin.InputUserId,
                    SndaId = loginResult.OauthLogin.SndaId,
                };

                accountToSave.AccountType = loginType switch
                {
                    LoginType.WeGameSid => XivAccountType.WeGameSid,
                    LoginType.WeGameToken => XivAccountType.WeGame,
                    LoginType.SdoStatic or LoginType.SdoSlide or LoginType.SdoQrCode => XivAccountType.Sdo
                };

                accountToSave.AreaName = Area.AreaName;

                if (doingAutoLogin && accountToSave.AccountType != XivAccountType.WeGameSid)
                {
                    accountToSave.AutoLoginSessionKey = await this.App.Accounts.Encrypt(loginResult.OauthLogin.AutoLoginSessionKey);
                    if (finalLoginType == LoginType.SdoStatic)
                    {
                        accountToSave.Password = await this.App.Accounts.Encrypt(serect);
                    }
                }

                if (accountToSave.AccountType == XivAccountType.WeGameSid)
                {
                    accountToSave.TestSID = await this.App.Accounts.Encrypt(serect);
                    //accountToSave.TestSID = await AccountManager.CredProvider.Encrypt("password");
                }
                accountToSave.GenerateId();
                this.App.Accounts.AddAccount(accountToSave);
                this.App.Accounts.CurrentAccount = accountToSave;
                this.App.Accounts.Save();
            }
        }
            
        Log.Information("[LR] {State} {NumPatches} {Playable}",
                        loginResult.State,
                        loginResult.PendingPatches?.Length,
                        loginResult.OauthLogin?.Playable);

        await this.App.Accounts.CredProvider.ClearCache();

        return await TryProcessLoginResult(loginResult, false, action).ConfigureAwait(false);
    }

    private async Task<Launcher.LoginResult> TryLoginToGame(LoginType type, LoginType fallbackLoginType, string username, string secret, bool autoLogin, LoginAction action)
    {
        try
        {
            Area = loginFrame.Area;
            if (Area == null)
            {
                throw new Exception("Area is not selected");
            }
            var enableUidCache = App.Settings.IsUidCacheEnabled ?? false;
            var gamePath = App.Settings.GamePath!;
            var checkResult = await App.Launcher.CheckGameUpdate(Area, gamePath, action == LoginAction.Repair);
            if (checkResult.State == Launcher.LoginState.NeedsPatchGame || action == LoginAction.Repair)
                return checkResult;
            
            if (type == LoginType.AutoLoginSession)
            {
                try
                {
                    return await this.App.Launcher.LoginBySessionKey(username, autoLoginSessionKey: secret).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    Log.Error("LoginBySessionKey failed, fallback to {fallbackLoginType}:{ex}", fallbackLoginType, e);
                    type = fallbackLoginType;
                }
            }
            

            switch (type)
            {
                case LoginType.SdoStatic:
                    return await this.App.Launcher.LoginBySdoStatic(username, password: secret).ConfigureAwait(false);

                case LoginType.SdoSlide:
                    return await this.App.Launcher.LoginBySlide(username, autoLogin, this.loginCts, (code) =>
                    {
                        Log.Information($"叨鱼确认码:{code}");
                        this.LoginMessage = $"确认码: {code}";
                        this.App.StartLoading("正在登录...",this.LoginMessage);
                    }).ConfigureAwait(false);

                case LoginType.SdoQrCode:
                    return await this.App.Launcher.LoginByScanQrCode(autoLogin, this.loginCts, (qrBytes) =>
                    {
                        this.App.qrBytes = qrBytes;
                        this.App.AskForQr();
                        new Task(() =>
                        {
                            App.WaitForQr(() => CancelLogin());
                        }).Start();
                    }).ConfigureAwait(false);

                case LoginType.WeGameToken:
                    return await this.App.Launcher.LoginByWeGameToken(username, token: secret, autoLogin).ConfigureAwait(false);

                case LoginType.WeGameSid:
                    return await this.App.Launcher.LoginBySid(username, sid: secret).ConfigureAwait(false);

                default:
                    throw new Exception($"Unknown LoginType:{type}");
            }
            /*
            string? autoLoginSessionKey = null;
            var autoLogin = string.IsNullOrEmpty(secret) && this.loginFrame.IsAutoLogin;
            try
            {
                if (autoLogin)
                {
                    autoLoginSessionKey = App.Accounts.CurrentAccount?.AutoLoginSessionKey;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Could not get auto login session key");
            }

            return await App.Launcher.Login(
                username,
                secret,
                (state, msg) =>
                {
                    Log.Information(msg);
                    switch (state)
                    {
                        case Launcher.SdoLoginState.GotQRCode:
                            App.AskForQr();
                            new Task(() =>
                            {
                                App.WaitForQr(() => App.Launcher.CancelLogin());
                            }).Start();
                            break;
                        case Launcher.SdoLoginState.LoginSucess:
                        case Launcher.SdoLoginState.LoginFail:
                        case Launcher.SdoLoginState.OutTime:
                            App.CloseQr();
                            break;
                    }
                },
                action == LoginAction.ForceQR,
                autoLogin,
                autoLoginSessionKey
            ).ConfigureAwait(false);
            */
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not login to game");
            throw;
        }
    }

    private async Task<bool> TryProcessLoginResult(Launcher.LoginResult loginResult, bool isSteam, LoginAction action)
    {
        // Format error message in the way OauthLoginException expects.
        var preErrorMsg = "window.external.user(\"login=auth,ng,err,";
        var postErrorMsg = "\");";

        if (loginResult.State == Launcher.LoginState.NoService)
        {
            throw new OauthLoginException(preErrorMsg + "No service account or subscription" + postErrorMsg);
        }

        if (loginResult.State == Launcher.LoginState.NoTerms)
        {
            throw new OauthLoginException(preErrorMsg + "Need to accept terms of use" + postErrorMsg);
        }

        /*
         * The server requested us to patch Boot, even though in order to get to this code, we just checked for boot patches.
         *
         * This means that something or someone modified boot binaries without our involvement.
         * We have no way to go back to a "known" good state other than to do a full reinstall.
         *
         * This has happened multiple times with users that have viruses that infect other EXEs and change their hashes, causing the update
         * server to reject our boot hashes.
         *
         * In the future we may be able to just delete /boot and run boot patches again, but this doesn't happen often enough to warrant the
         * complexity and if boot is fucked game probably is too.
         */
        if (loginResult.State == Launcher.LoginState.NeedsPatchBoot)
        {
            /*
            CustomMessageBox.Show(
                Loc.Localize("EverythingIsFuckedMessage",
                    "Certain essential game files were modified/broken by a third party and the game can neither update nor start.\nYou have to reinstall the game to continue.\n\nIf this keeps happening, please contact us via Discord."),
                "Error", MessageBoxButton.OK, MessageBoxImage.Error, parentWindow: _window);
                */

            throw new OauthLoginException(preErrorMsg + "Boot conflict, need reinstall" + postErrorMsg);
        }

        if (action == LoginAction.Repair)
        {
            try
            {
                if (!await RepairGame(loginResult).ConfigureAwait(false))
                    return false;

                App.ShowMessageBlocking("游戏修复完毕，请重新登陆。");
                return false;
            }
            catch (Exception)
            {
                /*
                 * We should never reach here.
                 * If server responds badly, then it should not even have reached this point, as error cases should have been handled before.
                 * If RepairGame was unsuccessful, then it should have handled all of its possible errors, instead of propagating it upwards.
                 */
                //CustomMessageBox.Builder.NewFrom(ex, "TryProcessLoginResult/Repair").WithParentWindow(_window).Show();
                throw;
            }
        }

        if (loginResult.State == Launcher.LoginState.NeedsPatchGame)
        {
            if (!await InstallGamePatch(loginResult).ConfigureAwait(false))
            {
                Log.Error("patchSuccess != true");
                return false;
            }

            App.ShowMessageBlocking("游戏更新完毕，请重新登陆。");
            return false;
        }

        if (loginResult.State == Launcher.LoginState.NeedRetry)
        {
            App.ShowMessageBlocking(
                Loc.Localize("LoginCancelled",
                    "登录过程被取消。"));

            return false;
        }

        if (action == LoginAction.GameNoLaunch)
        {
            App.ShowMessageBlocking(
                Loc.Localize("LoginNoStartOk",
                    "An update check was executed and any pending updates were installed."), "XIVLauncherCN");

            return false;
        }


        // #if !DEBUG
        //         bool? gateStatus = null;
        //         try
        //         {
        //             // TODO: Also apply the login status fix here
        //             var gate = await App.Launcher.GetGateStatus(App.Settings.ClientLanguage ?? ClientLanguage.English).ConfigureAwait(false);
        //             gateStatus = gate.Status;
        //         }
        //         catch (Exception ex)
        //         {
        //             Log.Error(ex, "Could not obtain gate status");
        //         }
        //         switch (gateStatus)
        //         {
        //             case null:
        //                 App.ShowMessageBlocking("Login servers could not be reached or maintenance is in progress. This might be a problem with your connection.");
        //                 return false;
        //             case false:
        //                 App.ShowMessageBlocking("Maintenance is in progress.");
        //                 return false;
        //         }
        // #endif

        Debug.Assert(loginResult.State == Launcher.LoginState.Ok);

        while (true)
        {
            List<Exception> exceptions = new();

            try
            {
                if (loginResult.OauthLogin.SessionId.IsNullOrEmpty() || loginResult.OauthLogin.SndaId.IsNullOrEmpty())
                {
                    Log.Error($"SID或SNDAID为空，取消登录");
                    App.ShowMessageBlocking("SID或SNDAID为空，取消登录");
                    return false;
                }

                using var process = await StartGameAndAddon(loginResult, isSteam, action == LoginAction.GameNoDalamud, action == LoginAction.GameNoPlugins, action == LoginAction.GameNoThirdparty).ConfigureAwait(false);

                if (process is null)
                    throw new InvalidOperationException("Could not obtain Process Handle");

                if (process.ExitCode != 0 && (App.Settings.TreatNonZeroExitCodeAsFailure ?? false))
                {
                    throw new InvalidOperationException("Game exited with non-zero exit code");

                }

                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "StartGameAndError resulted in an exception.");

                exceptions.Add(ex);
                throw;
            }

        }
    }

    public async Task<Process> StartGameAndAddon(Launcher.LoginResult loginResult, bool isSteam, bool forceNoDalamud, bool noPlugins , bool noThird)
    {
        var dalamudOk = false;

        IDalamudRunner dalamudRunner;
        IDalamudCompatibilityCheck dalamudCompatCheck;

        switch (Environment.OSVersion.Platform)
        {
            case PlatformID.Win32NT:
                dalamudRunner = new WindowsDalamudRunner();
                dalamudCompatCheck = new WindowsDalamudCompatibilityCheck();
                break;

            case PlatformID.Unix:
                dalamudRunner = new UnixDalamudRunner(Program.CompatibilityTools, Program.DotnetRuntime);
                dalamudCompatCheck = new UnixDalamudCompatibilityCheck();
                break;

            default:
                throw new NotImplementedException();
        }

        Troubleshooting.LogTroubleshooting();

        var dalamudLauncher = new DalamudLauncher(
            dalamudRunner,
            Program.DalamudUpdater,
            App.Settings.DalamudLoadMethod.GetValueOrDefault(DalamudLoadMethod.DllInject),
            App.Settings.GamePath,
            App.Storage.Root,
            App.Storage.GetFolder("logs"),
            App.Settings.ClientLanguage ?? ClientLanguage.ChineseSimplified,
            App.Settings.DalamudLoadDelay,
            false,
            false,
            noThird,
            Troubleshooting.GetTroubleshootingJson()
        );

        try
        {
            dalamudCompatCheck.EnsureCompatibility();
        }
        catch (IDalamudCompatibilityCheck.NoRedistsException ex)
        {
            Log.Error(ex, "No Dalamud Redists found");

            throw;
            /*
            CustomMessageBox.Show(
                Loc.Localize("DalamudVc2019RedistError",
                    "The XIVLauncher in-game addon needs the Microsoft Visual C++ 2015-2019 redistributable to be installed to continue. Please install it from the Microsoft homepage."),
                "XIVLauncher", MessageBoxButton.OK, MessageBoxImage.Exclamation, parentWindow: _window);
                */
        }
        catch (IDalamudCompatibilityCheck.ArchitectureNotSupportedException ex)
        {
            Log.Error(ex, "Architecture not supported");

            throw;
            /*
            CustomMessageBox.Show(
                Loc.Localize("DalamudArchError",
                    "Dalamud cannot run your computer's architecture. Please make sure that you are running a 64-bit version of Windows.\nIf you are using Windows on ARM, please make sure that x64-Emulation is enabled for XIVLauncher."),
                "XIVLauncher", MessageBoxButton.OK, MessageBoxImage.Exclamation, parentWindow: _window);
                */
        }

        if (App.Settings.DalamudEnabled.GetValueOrDefault(true) && !forceNoDalamud)
        {
            try
            {
                App.StartLoading("等待 Dalamud 准备就绪...", "这可能需要一点时间。请稍等！");
                dalamudOk = dalamudLauncher.HoldForUpdate(App.Settings.GamePath) == DalamudLauncher.DalamudInstallState.Ok;
            }
            catch (DalamudRunnerException ex)
            {
                Log.Error(ex, "Couldn't ensure Dalamud runner");

                var runnerErrorMessage = Loc.Localize("DalamudRunnerError",
                    "Could not launch Dalamud successfully. This might be caused by your antivirus.\nTo prevent this, please add an exception for the folder \"%AppData%\\XIVLauncher\\addons\".");

                throw;
                /*
                CustomMessageBox.Builder
                                .NewFrom(runnerErrorMessage)
                                .WithImage(MessageBoxImage.Error)
                                .WithButtons(MessageBoxButton.OK)
                                .WithShowHelpLinks()
                                .WithParentWindow(_window)
                                .Show();
                                */
            }
        }

        IGameRunner runner;

        // Set LD_PRELOAD to value of XL_PRELOAD if we're running as a steam compatibility tool.
        // This check must be done before the FixLDP check so that it will still work.
        if (CoreEnvironmentSettings.IsSteamCompatTool)
        {
            var ldpreload = System.Environment.GetEnvironmentVariable("LD_PRELOAD") ?? "";
            var xlpreload = System.Environment.GetEnvironmentVariable("XL_PRELOAD") ?? "";
            ldpreload = (ldpreload + ":" + xlpreload).Trim(':');
            if (!string.IsNullOrEmpty(ldpreload))
                System.Environment.SetEnvironmentVariable("LD_PRELOAD", ldpreload);
        }

        // Hack: Force C.utf8 to fix incorrect unicode paths
        if (App.Settings.FixLocale == true && !string.IsNullOrEmpty(Program.CType))
        {
            System.Environment.SetEnvironmentVariable("LC_ALL", Program.CType);
            System.Environment.SetEnvironmentVariable("LC_CTYPE", Program.CType);
        }

        // Hack: Strip out gameoverlayrenderer.so entries from LD_PRELOAD
        if (App.Settings.FixLDP == true)
        {
            var ldpreload = CoreEnvironmentSettings.GetCleanEnvironmentVariable("LD_PRELOAD", "gameoverlayrenderer.so");
            System.Environment.SetEnvironmentVariable("LD_PRELOAD", ldpreload);
        }

        // Hack: XMODIFIERS=@im=null
        if (App.Settings.FixIM == true)
        {
            System.Environment.SetEnvironmentVariable("XMODIFIERS", "@im=null");
        }

        // Hack: Fix libicuuc dalamud crashes
        if (App.Settings.FixError127 == true)
        {
            System.Environment.SetEnvironmentVariable("DOTNET_SYSTEM_GLOBALIZATION_USENLS", "true");
        }

        // Deal with "Additional Arguments". VAR=value %command% -args
        var launchOptions = (App.Settings.AdditionalArgs ?? string.Empty).Split("%command%", 2);
        var launchEnv = "";
        var gameArgs = "";

        // If there's only one launch option (no %command%) figure out whether it's args or env variables.
        if (launchOptions.Length == 1)
        {
            if (launchOptions[0].StartsWith('-'))
                gameArgs = launchOptions[0];
            else
                launchEnv = launchOptions[0];
        }
        else
        {
            launchEnv = launchOptions[0] ?? "";
            gameArgs = launchOptions[1] ?? "";
        }

        if (!string.IsNullOrEmpty(launchEnv))
        {
            foreach (var envvar in launchEnv.Split(null))
            {
                if (!envvar.Contains('=')) continue;    // ignore entries without an '='
                var kvp = envvar.Split('=', 2);
                if (kvp[0].EndsWith('+'))               // if key ends with +, then it's actually key+=value
                {
                    kvp[0] = kvp[0].TrimEnd('+');
                    kvp[1] = (System.Environment.GetEnvironmentVariable(kvp[0]) ?? "") + kvp[1];
                }
                System.Environment.SetEnvironmentVariable(kvp[0], kvp[1]);
            }
        }

        if (Environment.OSVersion.Platform == PlatformID.Win32NT)
        {
            runner = new WindowsGameRunner(dalamudLauncher, dalamudOk, Program.DalamudUpdater.Runtime);
        }
        else if (Environment.OSVersion.Platform == PlatformID.Unix || Environment.OSVersion.Platform == PlatformID.MacOSX)
        {
            if (App.Settings.WineStartupType == WineStartupType.Custom)
            {
                if (App.Settings.WineBinaryPath == null)
                    throw new InvalidOperationException("未设置自定义 wine 二进制路径。");
                else if (!Directory.Exists(App.Settings.WineBinaryPath))
                    throw new InvalidOperationException("自定义 wine 二进制路径无效：没有这样的目录。\n" +
                        "仔细检查路径是否有拼写错误： " + App.Settings.WineBinaryPath);
                else if (!File.Exists(Path.Combine(App.Settings.WineBinaryPath, "wine64")))
                    throw new InvalidOperationException("自定义 wine 二进制路径无效：在该位置未找到 wine64。\n" +
                        "仔细检查路径是否有拼写错误：" + App.Settings.WineBinaryPath);
            }

            var signal = new ManualResetEvent(false);
            var isFailed = false;

            var _ = Task.Run(async () =>
            {
                var tempPath = App.Storage.GetFolder("temp");
                var winver = (App.Settings.SetWin7 ?? true) ? "win7" : "win10";

                await Program.CompatibilityTools.EnsureTool(tempPath).ConfigureAwait(false);
                Program.CompatibilityTools.RunInPrefix($"winecfg /v {winver}");

                var gameFixApply = new GameFixApply(App.Settings.GamePath, App.Settings.GameConfigPath, Program.CompatibilityTools.Settings.Prefix, tempPath);
                gameFixApply.UpdateProgress += (text, hasProgress, progress) =>
                {
                    App.LoadingPage.Line1 = "正在应用游戏特定的修复...";
                    App.LoadingPage.Line2 = text;
                    App.LoadingPage.Line3 = "这可能需要一点时间。请稍等！";
                    App.LoadingPage.IsIndeterminate = !hasProgress;
                    App.LoadingPage.Progress = progress;
                };

                gameFixApply.Run();
            }).ContinueWith(t =>
            {
                isFailed = t.IsFaulted || t.IsCanceled;

                if (isFailed)
                    Log.Error(t.Exception, "Couldn't ensure compatibility tool");

                signal.Set();
            });

            App.StartLoading("正在安装兼容性工具...", "这可能需要一点时间。请稍等！");
            signal.WaitOne();
            signal.Dispose();

            if (isFailed)
                return null!;

            App.StartLoading("正在开始游戏...", "玩得开心！");

            runner = new UnixGameRunner(Program.CompatibilityTools, dalamudLauncher, dalamudOk);

            // SE has its own way of encoding spaces when encrypting arguments, which interferes 
            // with quoting, but they are necessary when passing paths unencrypted
            var userPath = Program.CompatibilityTools.UnixToWinePath(App.Settings.GameConfigPath!.FullName);
            if (App.Settings.IsEncryptArgs.GetValueOrDefault(true))
                gameArgs += $" UserPath={userPath}";
            else
                gameArgs += $" UserPath=\"{userPath}\"";

            gameArgs = gameArgs.Trim();
        }
        else
        {
            throw new NotImplementedException();
        }

        // We won't do any sanity checks here anymore, since that should be handled in StartLogin
        // var launchedProcess = App.Launcher.LaunchGame(runner,
        //     loginResult.UniqueId,
        //     loginResult.OauthLogin.Region,
        //     loginResult.OauthLogin.MaxExpansion,
        //     isSteam,
        //     gameArgs,
        //     App.Settings.GamePath,
        //     App.Settings.IsDx11 ?? true,
        //     App.Settings.ClientLanguage.GetValueOrDefault(ClientLanguage.English),
        //     App.Settings.IsEncryptArgs.GetValueOrDefault(true),
        //     App.Settings.DpiAwareness.GetValueOrDefault(DpiAwareness.Unaware));

        var launchedProcess = App.Launcher.LaunchGameSdo(runner,
            loginResult.OauthLogin.SessionId,
            loginResult.OauthLogin.SndaId,
            Area.Areaid,
            Area.AreaLobby,
            Area.AreaGm,
            Area.AreaConfigUpload,
            App.Settings.AdditionalArgs,
            App.Settings.GamePath,
            false,
            App.Settings.DpiAwareness.GetValueOrDefault(DpiAwareness.Unaware));

        // Hide the launcher if not Steam Deck or if using as a compatibility tool (XLM)
        // Show the Steam Deck prompt if on steam deck and not using as a compatibility tool
        if (!Program.IsSteamDeckHardware || CoreEnvironmentSettings.IsSteamCompatTool)
        {
            Hide();
        }
        else
        {
            App.State = LauncherApp.LauncherState.SteamDeckPrompt;
        }

        if (launchedProcess == null)
        {
            Log.Information("GameProcess was null...");
            IsLoggingIn = false;
            return null!;
        }

        var addonMgr = new AddonManager();

        try
        {
            App.Settings.Addons ??= new List<AddonEntry>();

            var addons = App.Settings.Addons.Where(x => x.IsEnabled).Select(x => x.Addon).Cast<IAddon>().ToList();

            addonMgr.RunAddons(launchedProcess.Id, addons);
        }
        catch (Exception)
        {
            /*
            CustomMessageBox.Builder
                            .NewFrom(ex, "Addons")
                            .WithAppendText("\n\n")
                            .WithAppendText(Loc.Localize("AddonLoadError",
                                "This could be caused by your antivirus, please check its logs and add any needed exclusions."))
                            .WithParentWindow(_window)
                            .Show();
                            */

            IsLoggingIn = false;

            addonMgr.StopAddons();
            throw;
        }

        Log.Debug("Waiting for game to exit");

        await Task.Run(() => launchedProcess!.WaitForExit()).ConfigureAwait(false);

        Log.Verbose("Game has exited");

        if (addonMgr.IsRunning)
            addonMgr.StopAddons();

        try
        {
            if (App.Steam?.IsValid == true)
            {
                App.Steam.Shutdown();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not shut down Steam");
        }

        return launchedProcess!;
    }
/*
    private void PersistAccount(string username, string password, bool isOtp, bool isSteam)
    {
        if (App.Accounts.CurrentAccount != null && App.Accounts.CurrentAccount.UserName.Equals(username, StringComparison.Ordinal) &&
            App.Accounts.CurrentAccount.Password != password &&
            App.Accounts.CurrentAccount.SavePassword)
            App.Accounts.UpdatePassword(App.Accounts.CurrentAccount, password);

        if (App.Accounts.CurrentAccount == null ||
            App.Accounts.CurrentAccount.Id != $"{username}-{isOtp}-{isSteam}")
        {
            var accountToSave = new XivAccount(username)
            {
                Password = password,
                SavePassword = true,
                UseOtp = isOtp,
                UseSteamServiceAccount = isSteam
            };

            App.Accounts.AddAccount(accountToSave);

            App.Accounts.CurrentAccount = accountToSave;
        }
    }
    */

    private async Task<bool> HandleBootCheck()
    {
        // CN
        return true;
        try
        {
            if (App.Settings.PatchPath is { Exists: false })
            {
                App.Settings.PatchPath = null;
            }

            App.Settings.PatchPath ??= new DirectoryInfo(Path.Combine(Paths.RoamingPath, "patches"));

            PatchListEntry[] bootPatches;

            try
            {
                bootPatches = await App.Launcher.CheckBootVersion(App.Settings.GamePath!).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Unable to check boot version");
                App.ShowMessage(
                    Loc.Localize("CheckBootVersionError",
                        "XIVLauncher was not able to check the boot version for the select game installation. This can happen if a maintenance is currently in progress or if your connection to the version check server is not available. Please report this error if you are able to login with the official launcher, but not XIVLauncher."),
                    "XIVLauncher");

                return false;
            }

            if (bootPatches.Length == 0)
                return true;

            return await TryHandlePatchAsync(Repository.Boot, bootPatches, "").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            App.ShowExceptionBlocking(ex, "PatchBoot");
            Environment.Exit(0);

            return false;
        }
    }

    private Task<bool> InstallGamePatch(Launcher.LoginResult loginResult)
    {
        Debug.Assert(loginResult.State == Launcher.LoginState.NeedsPatchGame,
            "loginResult.State == Launcher.LoginState.NeedsPatchGame ASSERTION FAILED");

        Debug.Assert(loginResult.PendingPatches != null, "loginResult.PendingPatches != null ASSERTION FAILED");

        return TryHandlePatchAsync(Repository.Ffxiv, loginResult.PendingPatches, loginResult.UniqueId);
    }

    private async Task<bool> TryHandlePatchAsync(Repository repository, PatchListEntry[] pendingPatches, string sid)
    {
        // BUG(goat): This check only behaves correctly on Windows - the mutex doesn't seem to disappear on Linux, .NET issue?
#if WIN32
        using var mutex = new Mutex(false, "XivLauncherIsPatching");

        if (!mutex.WaitOne(0, false))
        {
            App.ShowMessageBlocking(Loc.Localize("PatcherAlreadyInProgress", "XIVLauncher is already patching your game in another instance. Please check if XIVLauncher is still open."),
                "XIVLauncher");
            Environment.Exit(0);
            return false; // This line will not be run.
        }
#endif

        if (GameHelpers.CheckIsGameOpen())
        {
            App.ShowMessageBlocking(
                Loc.Localize("GameIsOpenError",
                    "游戏和/或官方启动器已打开。如果是这种情况，XIVLauncher 无法修补游戏。\n请关闭官方启动器并重试。"),
                "XIVLauncherCN");

            return false;
        }

        using var installer = new PatchInstaller(App.Settings.KeepPatches ?? false);
        Program.Patcher = new PatchManager(App.Settings.PatchAcquisitionMethod ?? AcquisitionMethod.Aria, App.Settings.PatchSpeedLimit, repository, pendingPatches, App.Settings.GamePath,
            App.Settings.PatchPath, installer, App.Launcher, sid);
        Program.Patcher.OnFail += PatcherOnFail;
        installer.OnFail += this.InstallerOnFail;

        /*
        Hide();

        PatchDownloadDialog progressDialog = _window.Dispatcher.Invoke(() =>
        {
            var d = new PatchDownloadDialog(patcher);
            if (_window.IsVisible)
                d.Owner = _window;
            d.Show();
            d.Activate();
            return d;
        });
        */

        this.App.StartLoading($"正在更新 {repository.ToString().ToLowerInvariant()}...", canCancel: false, isIndeterminate: false);

        try
        {
            var token = new CancellationTokenSource();
            var statusThread = new Thread(UpdatePatchStatus);

            statusThread.Start();

            void UpdatePatchStatus()
            {
                while (!token.IsCancellationRequested)
                {
                    Thread.Sleep(30);

                    App.LoadingPage.Line2 = string.Format("正在处理 {0}/{1}", Program.Patcher.CurrentInstallIndex, Program.Patcher.Downloads.Count);
                    App.LoadingPage.Line3 = string.Format("剩余 {0} (下载速度 {1}/s)", ApiHelpers.BytesToString(Program.Patcher.AllDownloadsLength < 0 ? 0 : Program.Patcher.AllDownloadsLength),
                        ApiHelpers.BytesToString(Program.Patcher.Speeds.Sum()));

                    App.LoadingPage.Progress = Program.Patcher.CurrentInstallIndex / (float)Program.Patcher.Downloads.Count;
                }
            }

            try
            {
                var aria2LogFile = new FileInfo(Path.Combine(App.Storage.GetFolder("logs").FullName, "launcher.log"));
                await Program.Patcher.PatchAsync(aria2LogFile, false).ConfigureAwait(false);
            }
            finally
            {
                token.Cancel();
                statusThread.Join(TimeSpan.FromMilliseconds(1000));
            }

            return true;
        }
        catch (PatchInstallerException ex)
        {
            var message = Loc.Localize("PatchManNoInstaller",
                "补丁安装程序无法正确启动。\n{0}\n\n如果您拒绝了访问，请重试。如果此问题仍然存在，请通过QQ频道与我们联系。");

            App.ShowMessageBlocking(string.Format(message, ex.Message), "XIVLauncherCN Error");
        }
        catch (NotEnoughSpaceException sex)
        {
            switch (sex.Kind)
            {
                case NotEnoughSpaceException.SpaceKind.Patches:
                    App.ShowMessageBlocking(
                        string.Format(
                            Loc.Localize("FreeSpaceError",
                                "您的驱动器上没有足够的空间来下载所有补丁。\\n\\n您可以在 XIVLauncher 设置中更改补丁的下载位置。\\n\\n需要：{0}\\n剩余：{1}"),
                            ApiHelpers.BytesToString(sex.BytesRequired), ApiHelpers.BytesToString(sex.BytesFree)), "XIVLauncherCN Error");
                    break;

                case NotEnoughSpaceException.SpaceKind.AllPatches:
                    App.ShowMessageBlocking(
                        string.Format(
                            Loc.Localize("FreeSpaceErrorAll",
                                "您的驱动器上没有足够的空间来下载所有补丁。\n\n您可以在 XIVLauncher 设置中更改补丁的下载位置。\n\n需要：{0}\n剩余：{1}"),
                            ApiHelpers.BytesToString(sex.BytesRequired), ApiHelpers.BytesToString(sex.BytesFree)), "XIVLauncherCN Error");
                    break;

                case NotEnoughSpaceException.SpaceKind.Game:
                    App.ShowMessageBlocking(
                        string.Format(
                            Loc.Localize("FreeSpaceGameError",
                                "您的驱动器上没有足够的空间来安装补丁。\n\n您可以在设置中更改游戏的安装位置。\n\n需要：{0}\\n剩余：{1}"),
                            ApiHelpers.BytesToString(sex.BytesRequired), ApiHelpers.BytesToString(sex.BytesFree)), "XIVLauncherCN Error");
                    break;

                default:
                    Debug.Assert(false, "HandlePatchAsync:Invalid NotEnoughSpaceException.SpaceKind value.");
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during patching");
            App.ShowExceptionBlocking(ex, "HandlePatchAsync");
        }
        finally
        {
            App.State = LauncherApp.LauncherState.Main;
        }

        return false;
    }

    private void PatcherOnFail(PatchListEntry patch, string context)
    {
        var dlFailureLoc = Loc.Localize("PatchManDlFailure",
            "XIVLauncher 无法验证已下载的游戏文件。请重新启动并重试。\n\n这通常表示您的互联网连接存在问题。\n如果此错误仍然存在，请尝试使用设置为中国的 VPN。\n\n\nContext: {0}\n{1}");

        var sdoPatchMissingFailureLoc = Loc.Localize("SdoPatchMissing",
            "游戏补丁列表的早期补丁被删除，无法通过补丁安装完整游戏，请手动安装游戏客户端并在设置中设定包含 game 文件夹的游戏路径。\nContext: {0}\n{1}");
        var errorMsg = (patch.VersionId.StartsWith("2014.03.24")) ? sdoPatchMissingFailureLoc : dlFailureLoc;
        App.ShowMessageBlocking(string.Format(errorMsg, "Problem", patch.VersionId), "XIVLauncherCN Error");

        Environment.Exit(0);
    }

    private void InstallerOnFail()
    {
        App.ShowMessageBlocking(
            Loc.Localize("PatchInstallerInstallFailed", "补丁安装程序遇到错误。\n请报告此错误。\n\n请重试或使用官方启动器。"),
            "XIVLauncherCN Error");

        Environment.Exit(0);
    }

    private async Task<bool> RepairGame(Launcher.LoginResult loginResult)
    {
        var doLogin = false;

        // BUG(goat): This check only behaves correctly on Windows - the mutex doesn't seem to disappear on Linux, .NET issue?
#if WIN32
        using var mutex = new Mutex(false, "XivLauncherIsPatching");

        if (!mutex.WaitOne(0, false))
        {
            App.ShowMessageBlocking(Loc.Localize("PatcherAlreadyInProgress", "XIVLauncher is already patching your game in another instance. Please check if XIVLauncher is still open."),
                "XIVLauncher");
            Environment.Exit(0);
            return false; // This line will not be run.
        }
#endif
        /*
        Debug.Assert(loginResult.PendingPatches != null, "loginResult.PendingPatches != null ASSERTION FAILED");
        Debug.Assert(loginResult.PendingPatches.Length != 0, "loginResult.PendingPatches.Length != 0 ASSERTION FAILED");
        */

        Log.Information("STARTING REPAIR");

        // TODO: bundle the PatchInstaller with xl-core on Windows and run this remotely
        var maxExpansion = 5;
        using var verify = new PatchVerifier(Program.CommonSettings, loginResult, TimeSpan.FromMilliseconds(100), maxExpansion, false);

        for (bool doVerify = true; doVerify;)
        {
            this.App.StartLoading($"正在修复...", canCancel: false, isIndeterminate: false);

            verify.Start();

            var timer = new Timer(new TimerCallback((object? obj) =>
            {
                switch (verify.State)
                {
                    // TODO: show more progress info here
                    case PatchVerifier.VerifyState.DownloadMeta:
                        this.App.LoadingPage.Line2 = $"{verify.CurrentFile}";
                        this.App.LoadingPage.Line3 = $"{Math.Min(verify.PatchSetIndex + 1, verify.PatchSetCount)}/{verify.PatchSetCount} - {ApiHelpers.BytesToString(verify.Progress)}/{ApiHelpers.BytesToString(verify.Total)}";
                        this.App.LoadingPage.Progress = (float)(verify.Total != 0 ? (float)verify.Progress / (float)verify.Total : 0.0);
                        break;

                    case PatchVerifier.VerifyState.VerifyAndRepair:
                        this.App.LoadingPage.Line2 = $"{verify.CurrentFile}";
                        this.App.LoadingPage.Line3 = $"{Math.Min(verify.PatchSetIndex + 1, verify.PatchSetCount)}/{verify.PatchSetCount} - {Math.Min(verify.TaskIndex + 1, verify.TaskCount)}/{verify.TaskCount} - {ApiHelpers.BytesToString(verify.Progress)}/{ApiHelpers.BytesToString(verify.Total)}";
                        this.App.LoadingPage.Progress = (float)(verify.Total != 0 ? (float)verify.Progress / (float)verify.Total : 0);
                        break;

                    default:
                        this.App.LoadingPage.Line2 = "";
                        this.App.LoadingPage.Line3 = $"{Math.Min(verify.TaskIndex + 1, verify.TaskCount)}/{verify.TaskCount}";
                        this.App.LoadingPage.Progress = (float)(verify.State == PatchVerifier.VerifyState.Done ? 1.0 : 0);
                        break;
                }
            }
            ));
            timer.Change(0, 250);

            await verify.WaitForCompletion().ConfigureAwait(false);
            timer.Dispose();
            this.App.StopLoading();

            switch (verify.State)
            {
                case PatchVerifier.VerifyState.Done:
                    // TODO: ask the user if they want to login or rerun after repair
                    App.ShowMessageBlocking(verify.NumBrokenFiles switch
                    {
                        0 => Loc.Localize("GameRepairSuccess0", "所有的游戏文件都是完整的。"),
                        1 => Loc.Localize("GameRepairSuccess1", "XIVLauncherCN 已成功修复 1 个游戏文件。"),
                        _ => string.Format(Loc.Localize("GameRepairSuccessPlural", "XIVLauncherCN 已成功修复 {0} 个游戏文件。"), verify.NumBrokenFiles),
                    });

                    doVerify = false;
                    break;

                case PatchVerifier.VerifyState.Error:
                    doLogin = false;

                    if (verify.LastException is NoVersionReferenceException)
                    {
                        App.ShowMessageBlocking(Loc.Localize("NoVersionReferenceError",
                                                        "您所使用的游戏版本尚无法通过 XIVLauncherCN 修复，因为尚无参考信息。\n请稍后重试。"));
                    }
                    else
                    {
                        App.ShowMessageBlocking(verify.LastException + "\n\n" + Loc.Localize("GameRepairError", "修复游戏文件时出现错误。\n您需要重新安装游戏。"));
                    }

                    doVerify = false;
                    break;

                case PatchVerifier.VerifyState.Cancelled:
                    doLogin = doVerify = false;
                    break;
            }
        }


        return doLogin;
    }

    private void Hide()
    {
        Program.HideWindow();
    }

    private void Reactivate()
    {
        IsLoggingIn = false;
        this.App.State = LauncherApp.LauncherState.Main;

        Program.ShowWindow();
    }
}
