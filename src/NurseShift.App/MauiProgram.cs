using Microsoft.Extensions.Logging;
using NurseShift.App.Features.History;
using NurseShift.App.Features.Import;
using NurseShift.App.Features.Onboarding;
using NurseShift.App.Features.Settings;
using NurseShift.App.Features.Shift;
using NurseShift.App.Images;
using NurseShift.App.Notifications;
using NurseShift.App.Services;
using NurseShift.Core.Interfaces;
using NurseShift.Core.Services;
using NurseShift.Infrastructure.GigaChat;
using NurseShift.Infrastructure.Persistence;

namespace NurseShift.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
        builder.Services.AddSingleton<IClock, SystemClock>();
        builder.Services.AddSingleton<ISecretStore, SecureSecretStore>();
        builder.Services.AddSingleton<ISettingsStore, PreferencesSettingsStore>();
        builder.Services.AddSingleton<IPhotoRetentionStore, PhotoRetentionStore>();
        builder.Services.AddSingleton<DeepLinkService>();
        builder.Services.AddSingleton<AndroidImageService>();
        builder.Services.AddSingleton<IImagePicker>(services => services.GetRequiredService<AndroidImageService>());
        builder.Services.AddSingleton<IImageProcessor>(services => services.GetRequiredService<AndroidImageService>());
        builder.Services.AddSingleton<IReminderScheduler, AndroidReminderScheduler>();
        builder.Services.AddSingleton<AndroidAppLockService>();
        builder.Services.AddSingleton<IAppLockService>(services => services.GetRequiredService<AndroidAppLockService>());

        var databasePath = Path.Combine(FileSystem.AppDataDirectory, "nurseshift.db");
        var photoRoot = Path.Combine(FileSystem.AppDataDirectory, "import-pages");
        builder.Services.AddSingleton<INurseShiftDbContextFactory>(services => new NurseShiftDbContextFactory(databasePath, services.GetRequiredService<ISecretStore>()));
        builder.Services.AddTransient<DatabaseInitializer>();
        builder.Services.AddTransient<IShiftService, ShiftService>();
        builder.Services.AddTransient<IImportService>(services => new ImportService(services.GetRequiredService<INurseShiftDbContextFactory>(), services.GetRequiredService<IClock>(),
            services.GetRequiredService<ISettingsStore>(), services.GetRequiredService<ISecretStore>(), services.GetRequiredService<IRecognitionService>(),
            services.GetRequiredService<IReminderScheduler>(), services.GetRequiredService<IImageProcessor>(), services.GetRequiredService<IPhotoRetentionStore>(), photoRoot));
        builder.Services.AddTransient<IPhotoAccess>(services => new PhotoAccess(services.GetRequiredService<INurseShiftDbContextFactory>(), photoRoot));
        builder.Services.AddTransient<IDataCleanupService>(services => new DataCleanupService(services.GetRequiredService<INurseShiftDbContextFactory>(), services.GetRequiredService<IShiftService>(),
            services.GetRequiredService<IClock>(), services.GetRequiredService<ISecretStore>(), services.GetRequiredService<ISettingsStore>(), photoRoot));
        builder.Services.AddHttpClient<GigaChatAuthClient>(client => client.Timeout = TimeSpan.FromSeconds(60));
        builder.Services.AddHttpClient<IRecognitionService, GigaChatRecognitionService>(client => client.Timeout = TimeSpan.FromSeconds(60));

        builder.Services.AddTransient<AppShell>();
        builder.Services.AddTransient<WelcomePage>(); builder.Services.AddTransient<WelcomeViewModel>();
        builder.Services.AddTransient<ShiftPage>(); builder.Services.AddTransient<ShiftViewModel>();
        builder.Services.AddTransient<NewShiftPage>(); builder.Services.AddTransient<NewShiftViewModel>();
        builder.Services.AddTransient<AssignmentPage>(); builder.Services.AddTransient<AssignmentViewModel>();
        builder.Services.AddTransient<HistoryPage>(); builder.Services.AddTransient<HistoryViewModel>();
        builder.Services.AddTransient<HistoryDetailPage>(); builder.Services.AddTransient<HistoryDetailViewModel>();
        builder.Services.AddTransient<SettingsPage>(); builder.Services.AddTransient<SettingsViewModel>();
        builder.Services.AddTransient<ImportPage>(); builder.Services.AddTransient<ImportViewModel>();
        builder.Services.AddTransient<ReviewPage>(); builder.Services.AddTransient<ReviewViewModel>();
        builder.Services.AddTransient<PhotoPage>(); builder.Services.AddTransient<PhotoViewModel>();
#if DEBUG
        builder.Logging.AddDebug();
#endif
        return builder.Build();
    }
}
