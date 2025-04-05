using System.Collections.ObjectModel;

using Castle.Core.Internal;

using Newtonsoft.Json;
using Serilog;

using SQLite;

using XIVLauncher.Common.Util;
using XIVLauncher.Core.Accounts.Cred;
using XIVLauncher.Core.Accounts.Cred.CredProviders;

namespace XIVLauncher.Core.Accounts;

public class AccountManager
{
    private readonly object syncRoot = new();
    private SQLiteConnection? db;
    public ObservableCollection<XivAccount> Accounts;

    public XivAccount? CurrentAccount
    {
        get { return Accounts.Count > 1 ? Accounts.FirstOrDefault(a => a.Id == Program.Config.CurrentAccountId) : Accounts.FirstOrDefault(); }
        set => Program.Config.CurrentAccountId = value?.Id;
    }

    private readonly CredData CredData;
    public CredType? CurrentCredType;
    public ICredProvider CredProvider { get; private set; }

    public AccountManager()
    {
        Load();

        var credPath = Program.storage.GetFile("cred.json").FullName;
        this.CredData = new CredData("XIVLauncherCN", credPath);

        Accounts.CollectionChanged += Accounts_CollectionChanged;
        ChangeCredType(Program.Config.CredType.GetValueOrDefault(CredType.CredentialManager));
    }

    public async Task<string> Encrypt(string text)
    {
        try
        {
            if (text is null)
                return null;

            if (this.CredProvider == null)
            {
                throw new Exception("CredProvider is null");
            }
            return await this.CredProvider.Encrypt(text);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to encrypt text");
        }
        return null;
    }

    public async Task<string> Decrypt(string text)
    {
        try
        {
            if (text is null)
                return null;

            if (this.CredProvider == null)
            {
                throw new Exception("CredProvider is null");
            }
            return await this.CredProvider.Decrypt(text);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to encrypt text");
        }
        return null;
    }
    
    public async void ChangeCredType(CredType? type)
    {
        if (type == this.CurrentCredType)
            return;
        var oldCred = this.CredProvider;
        var newCred = GetCredProvider(type.Value);
        var isSupported = await newCred.IsSupported();
        if (!isSupported)
        {
            Log.Error($"Cred type: {type} not supported, falling back to NoCred");
            newCred = new NoCred(this.CredData);
        }
        
        if (oldCred == null)
        {
            this.CurrentCredType = type;
            this.CredProvider = newCred;
            return;
        }

        var testText = EncryptionHelper.GetRandomHexString(32);
        var encrypted = await newCred.Encrypt(testText);
        var decrypted = await newCred.Decrypt(encrypted);
        if (testText != decrypted)
        {
            throw new Exception($"Cred type: {type} test failed");
        }

        Log.Information($"Change cred type from {this.CurrentCredType} to {type}");
        foreach (var item in Accounts)
        {
            if (item.AutoLoginSessionKey != null)
            {
                try
                {
                    var sessionKey = await oldCred.Decrypt(item.AutoLoginSessionKey);
                    item.AutoLoginSessionKey = await newCred.Encrypt(sessionKey);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, $"Failed to change {item.Id}.AutoLoginSessionKey");
                }
            }

            if (item.TestSID != null)
            {
                try
                {
                    var testSid = await oldCred.Decrypt(item.TestSID);
                    item.TestSID = await newCred.Encrypt(testSid);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, $"Failed to change {item.Id}.TestSID");
                }
            }
        }

        this.CurrentCredType = type;
        this.CredProvider = newCred;
        Log.Information($"Changed cred type from {this.CurrentCredType} to {type} successfully");
        Save();
    }

    private ICredProvider GetCredProvider(CredType type)
    {
        switch (type)
        {
            case CredType.CredentialManager:
                return new CredentialManager(this.CredData);
            case CredType.NoEncryption:
                return new NoCred(this.CredData);
        }
        return null;
    }

    private void Accounts_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        Save();
    }

    public void AddAccount(XivAccount account)
    {
        if (account.UserName.IsNullOrEmpty() || account.Id.IsNullOrEmpty())
        {
            throw new Exception($"UserName:{account.UserName} Id:{account.Id} 不能为空");
        }

        var existingAccount = Accounts.FirstOrDefault(a => a.Equals(account));

        Log.Verbose($"existingAccount: {existingAccount?.Id}");

        if (existingAccount != null)
        {
            Log.Verbose("Updating account...");
            existingAccount.Id = account.Id;
            existingAccount.Password = account.Password;
            existingAccount.AutoLogin = account.AutoLogin;
            existingAccount.AutoLoginSessionKey = account.AutoLoginSessionKey;
            existingAccount.TestSID = account.TestSID;
            existingAccount.AreaName = account.AreaName;
        }
        else
        {
            Accounts.Add(account);
        }
    }

    public void RemoveAccount(XivAccount account)
    {
        account.Password = string.Empty;
        Accounts.Remove(account);

        lock (this.syncRoot)
        {

            this.db.RunInTransaction(() =>
            {
                var record = this.db.Table<XivAccount>().FirstOrDefault(a => a.Id == account.Id);
                if (record != null)
                {
                    this.db.Delete(account);
                }
            });
        }
    }

    #region SaveLoad


    private static readonly string DatabasePath = Program.storage.GetFile("accounts.db").FullName;

    public void Save(XivAccount account)
    {
        lock (this.syncRoot)
        {
            this.db.RunInTransaction(() =>
            {
                var record = this.db.Table<XivAccount>().FirstOrDefault(a => a.Id == account.Id);
                if (record == null)
                {
                    this.db.Insert(account);
                }
                else
                {
                    record = account;
                    this.db.Update(record);
                }
            });
        }
    }

    public void Save()
    {
        foreach (var item in Accounts)
        {
            this.Save(item);
        }
    }

    public void SetupDb()
    {
        this.db = new SQLiteConnection(DatabasePath,
                                       SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create | SQLiteOpenFlags.FullMutex);
        this.db.CreateTable<XivAccount>();
    }

    public void Load()
    {
        try
        {
            this.SetupDb();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load VFS database, starting fresh");

            if (File.Exists(DatabasePath))
                File.Delete(DatabasePath);

            this.SetupDb();

        }

        // If the file is corrupted, this will be null anyway
        Accounts = new ObservableCollection<XivAccount>(this.db.Table<XivAccount>());

        foreach (var account in Accounts)
        {
            if (account.UserName.IsNullOrEmpty() || account.Id.IsNullOrEmpty())
            {
                Accounts.Remove(account);
            }
        }
    }


    #endregion
}
