using System.Text;

using KeySharp;

using Serilog;

namespace XIVLauncher.Core.Accounts.Cred.CredProviders;

public class CredentialManager : ICredProvider
{
    public string GetName() => "凭据管理器";
    public string GetDescription() => "使用系统自带的凭据管理器";

    private CredData Cred { get; init; }
    private EncryptionHelper? EncryptionHelper;
    public bool IsAvailable { get; private set; }
    private const string SERVICE = "SDOID";

    public CredentialManager(CredData cred)
    {
        this.Cred = cred;
        this.IsAvailable = SetDummyAndCheck();
    }
    
    public bool SetDummyAndCheck()
    {
        /*
         * We need to set a dummy entry here to ensure that libsecret unlocks the keyring.
         * This is a problem with libsecret: http://crbug.com/660005
         */
        try
        {
            const string DUMMY_NAME = "XIVLauncherCN";
            const string DUMMY_PW = "Otter loves you";

            Keyring.SetPassword(this.Cred.PackageName, SERVICE, DUMMY_NAME, DUMMY_PW);

            var saved = Keyring.GetPassword(this.Cred.PackageName, SERVICE, DUMMY_NAME);
            return saved == DUMMY_PW;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not init the keychain");
        }

        return false;
    }

    public async Task ClearCache()
    {
        this.EncryptionHelper = null;
    }

    public async Task<string?> Decrypt(string? text)
    {
        this.EncryptionHelper ??= GetEncryptionHelper();
        if (text == null)
        {
            return null;
        }
        return this.EncryptionHelper.DecryptString(text);
    }

    public async Task<string?> Encrypt(string? text)
    {
        this.EncryptionHelper ??= GetEncryptionHelper();
        if (text == null)
        {
            return null;
        }
        return this.EncryptionHelper.EncryptString(text);
    }

    public EncryptionHelper GetEncryptionHelper()
    {
        return new EncryptionHelper(Encoding.UTF8.GetBytes(GetPassword()), Convert.FromBase64String(Cred.LoginSalt));
    }

    public string GetPassword()
    {
        string credentialsPassword = null;
        try
        {
            credentialsPassword = Keyring.GetPassword(Cred.PackageName, SERVICE, Cred.Account);
        }
        catch (Exception ex)
        {
            Log.Error($"Could not retrieve password for {Cred.Account}");
            Log.Debug("Generating new password");
            var password = EncryptionHelper.GetRandomHexString(128);
            try
            {
                Log.Debug("Setting new password");
                Keyring.SetPassword(Cred.PackageName, SERVICE, Cred.Account, password);
            }
            catch (Exception)
            {
                Log.Error($"Could not set new password for {Cred.Account}");
            }
            return password;
        }

        return credentialsPassword;
    }

    public async Task<bool> IsSupported()
    {
        return this.IsAvailable;
    }

    public async Task Unregister()
    {
        Keyring.DeletePassword(Cred.PackageName, SERVICE, Cred.Account);
    }
}
