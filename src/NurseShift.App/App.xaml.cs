using NurseShift.App.Features.Onboarding;
using NurseShift.App.Resources.Strings;
using NurseShift.Core.Enums;
using NurseShift.Core.Interfaces;
using NurseShift.Infrastructure.Persistence;

namespace NurseShift.App;

public partial class App : Application
{
    private readonly IServiceProvider _services;
    private bool _initialized;
    private bool _unlocked;
    private bool _authenticating;
    private bool _credentialScreenShown;
    private Page? _protectedPage;

    public App(IServiceProvider services)
    {
        _services = services;
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState) => new(new ContentPage
    {
        Content = new VerticalStackLayout { VerticalOptions = LayoutOptions.Center, Children = { new ActivityIndicator { IsRunning = true }, new Label { Text = UiStrings.Loading, HorizontalTextAlignment = TextAlignment.Center } } }
    });

    protected override async void OnStart()
    {
        base.OnStart();
        if (_initialized)
        {
            await EnsureUnlockedAsync();
            return;
        }
        try
        {
            await _services.GetRequiredService<DatabaseInitializer>().InitializeAsync(CancellationToken.None);
            await _services.GetRequiredService<IDataCleanupService>().CleanupHistoryAsync(CancellationToken.None);
            await _services.GetRequiredService<IReminderScheduler>().SynchronizeAllFutureAsync(CancellationToken.None);
            var settings = await _services.GetRequiredService<ISettingsStore>().GetAsync(CancellationToken.None);
            ApplyTheme(settings.Theme);
            Windows[0].Page = settings.OnboardingCompleted ? _services.GetRequiredService<AppShell>() : _services.GetRequiredService<WelcomePage>();
            _initialized = true;
            await EnsureUnlockedAsync();
        }
        catch (Exception)
        {
            Windows[0].Page = new ContentPage { Content = new VerticalStackLayout { Padding = 24, Spacing = 16, VerticalOptions = LayoutOptions.Center,
                Children = { new Label { Text = UiStrings.Error(SafeErrorCode.StorageUnavailable), Style = (Style)Resources["SectionTitle"] }, new Label { Text = UiStrings.Get("StorageRecovery") } } } };
        }
    }

    protected override void OnSleep()
    {
        base.OnSleep();
        if (!_initialized)
            return;
        if (_authenticating)
            _credentialScreenShown = true;
        else
            _unlocked = false;
    }

    protected override async void OnResume()
    {
        base.OnResume();
        if (_credentialScreenShown)
        {
            _credentialScreenShown = false;
            return;
        }
        if (_initialized && !_unlocked)
            await EnsureUnlockedAsync();
    }

    private async Task EnsureUnlockedAsync()
    {
        if (_unlocked || _authenticating)
            return;
        var settings = await _services.GetRequiredService<ISettingsStore>().GetAsync(CancellationToken.None);
        if (!settings.LockWithDeviceCredential)
        {
            _unlocked = true;
            return;
        }
        _authenticating = true;
        _protectedPage ??= Windows[0].Page;
        Windows[0].Page = CreateLockPage();
        var unlocked = false;
        try
        {
            unlocked = await _services.GetRequiredService<IAppLockService>().AuthenticateAsync(CancellationToken.None);
        }
        catch (Exception)
        {
        }
        finally
        {
            _authenticating = false;
        }
        _unlocked = unlocked;
        if (unlocked)
        {
            Windows[0].Page = _protectedPage ?? (settings.OnboardingCompleted ? _services.GetRequiredService<AppShell>() : _services.GetRequiredService<WelcomePage>());
            _protectedPage = null;
        }
    }

    private ContentPage CreateLockPage() => new()
    {
        Content = new VerticalStackLayout
        {
            Padding = 24,
            Spacing = 16,
            VerticalOptions = LayoutOptions.Center,
            Children =
            {
                new Label { Text = UiStrings.Get("UnlockApp"), Style = (Style)Resources["ScreenTitle"], HorizontalTextAlignment = TextAlignment.Center },
                new Label { Text = UiStrings.Get("UnlockFailed"), HorizontalTextAlignment = TextAlignment.Center },
                new Button { Text = UiStrings.Get("Unlock"), Command = new Command(async () => await EnsureUnlockedAsync()) }
            }
        }
    };

    public static void ApplyTheme(Core.Enums.ThemePreference preference) => Current!.UserAppTheme = preference switch
    {
        Core.Enums.ThemePreference.Light => AppTheme.Light,
        Core.Enums.ThemePreference.Dark => AppTheme.Dark,
        _ => AppTheme.Unspecified
    };
}
