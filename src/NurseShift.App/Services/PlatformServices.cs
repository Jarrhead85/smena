using System.Security.Cryptography;
using NurseShift.Core.Enums;
using NurseShift.Core.Interfaces;
using NurseShift.Core.Models;

namespace NurseShift.App.Services;

public sealed class SecureSecretStore : ISecretStore
{
    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken) => await SecureStorage.GetAsync(key);

    public async Task SetAsync(string key, string value, CancellationToken cancellationToken) => await SecureStorage.SetAsync(key, value);

    public Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        SecureStorage.Remove(key);
        return Task.CompletedTask;
    }
}

public sealed class PreferencesSettingsStore : ISettingsStore
{
    private const string Prefix = "settings.";
    public Task<AppSettings> GetAsync(CancellationToken cancellationToken)
    {
        var defaults = AppSettings.Default;
        var value = new AppSettings(
            Preferences.Get(Prefix + "model", defaults.Model),
            Preferences.Get(Prefix + "scope", defaults.Scope),
            Preferences.Get(Prefix + "lead", defaults.AdvanceLeadMinutes),
            (ThemePreference)Preferences.Get(Prefix + "theme", (int)defaults.Theme),
            Preferences.Get(Prefix + "retention", defaults.HistoryRetentionDays ?? -1) is int days && days >= 0 ? days : null,
            Preferences.Get(Prefix + "photos", defaults.RetainPhotos),
            Preferences.Get(Prefix + "private", defaults.PrivateLockScreenNotifications),
            Preferences.Get(Prefix + "app_lock", defaults.LockWithDeviceCredential),
            Preferences.Get(Prefix + "onboarding", defaults.OnboardingCompleted),
            Preferences.Get(Prefix + "consent", defaults.ConsentKeyHash));
        return Task.FromResult(value);
    }

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        Preferences.Set(Prefix + "model", settings.Model);
        Preferences.Set(Prefix + "scope", settings.Scope);
        Preferences.Set(Prefix + "lead", settings.AdvanceLeadMinutes);
        Preferences.Set(Prefix + "theme", (int)settings.Theme);
        Preferences.Set(Prefix + "retention", settings.HistoryRetentionDays ?? -1);
        Preferences.Set(Prefix + "photos", settings.RetainPhotos);
        Preferences.Set(Prefix + "private", settings.PrivateLockScreenNotifications);
        Preferences.Set(Prefix + "app_lock", settings.LockWithDeviceCredential);
        Preferences.Set(Prefix + "onboarding", settings.OnboardingCompleted);
        Preferences.Set(Prefix + "consent", settings.ConsentKeyHash);
        return Task.CompletedTask;
    }
}

public sealed class PhotoRetentionStore(ISecretStore secretStore) : IPhotoRetentionStore
{
    private const string FileKeyName = "photos.encryption_key";

    public async Task FinalizeAsync(SourceImage image, bool retain, CancellationToken cancellationToken)
    {
        var encryptedPath = image.LocalPath + ".enc";
        if (retain && File.Exists(image.LocalPath))
        {
            var encodedKey = await secretStore.GetAsync(FileKeyName, cancellationToken);
            if (string.IsNullOrWhiteSpace(encodedKey))
            {
                encodedKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                await secretStore.SetAsync(FileKeyName, encodedKey, cancellationToken);
            }
            using var aes = Aes.Create();
            aes.Key = Convert.FromBase64String(encodedKey);
            aes.GenerateIV();
            await using var input = new FileStream(image.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            await using var output = new FileStream(encryptedPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            await output.WriteAsync(aes.IV, cancellationToken);
            await using var crypto = new CryptoStream(output, aes.CreateEncryptor(), CryptoStreamMode.Write);
            await input.CopyToAsync(crypto, cancellationToken);
            await crypto.FlushFinalBlockAsync(cancellationToken);
        }
        if (File.Exists(image.LocalPath))
            File.Delete(image.LocalPath);
        if (!retain && File.Exists(encryptedPath))
            File.Delete(encryptedPath);
    }
}

public sealed class DeepLinkService
{
    public event EventHandler<Guid>? AssignmentRequested;
    public Guid? PendingAssignmentId { get; private set; }

    public void OpenAssignment(Guid id)
    {
        PendingAssignmentId = id;
        AssignmentRequested?.Invoke(this, id);
    }

    public Guid? Consume()
    {
        var id = PendingAssignmentId;
        PendingAssignmentId = null;
        return id;
    }
}
