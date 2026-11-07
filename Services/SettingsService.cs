using System.Text.Json;
using VibeLauncher.Models;

namespace VibeLauncher.Services;

public static class SettingsService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static LauncherSettings Current { get; private set; } = new();

    public static void Load()
    {
        try
        {
            if (File.Exists(Paths.SettingsFile))
            {
                var json = File.ReadAllText(Paths.SettingsFile);
                Current = JsonSerializer.Deserialize<LauncherSettings>(json, Options) ?? new LauncherSettings();
            }
        }
        catch (Exception ex)
        {
            LogService.WriteCrash("Settings.Load", ex.ToString());
            Current = new LauncherSettings();
        }

        Current.Accounts ??= new List<Account>();
        if (Current.RamMb < 512) Current.RamMb = 4096;
    }

    public static void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(Current, Options);
            File.WriteAllText(Paths.SettingsFile, json);
        }
        catch (Exception ex)
        {
            LogService.WriteCrash("Settings.Save", ex.ToString());
        }
    }

    public static Account? SelectedAccount =>
        Current.Accounts.FirstOrDefault(a => a.Id == Current.SelectedAccountId)
        ?? Current.Accounts.FirstOrDefault();

    public static void UpsertAccount(Account account)
    {
        var existing = Current.Accounts.FirstOrDefault(a => a.Id == account.Id)
                       ?? Current.Accounts.FirstOrDefault(a =>
                           a.Username.Equals(account.Username, StringComparison.OrdinalIgnoreCase) && a.Type == account.Type);
        if (existing != null)
        {
            account.Id = existing.Id;
            Current.Accounts.Remove(existing);
        }
        Current.Accounts.Insert(0, account);
        Current.SelectedAccountId = account.Id;
        Save();
    }

    public static void RemoveAccount(Account account)
    {
        Current.Accounts.Remove(account);
        if (Current.SelectedAccountId == account.Id)
            Current.SelectedAccountId = Current.Accounts.FirstOrDefault()?.Id;
        Save();
    }
}
