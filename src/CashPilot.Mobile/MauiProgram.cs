using CashPilot.Mobile.Pages;
using CashPilot.Mobile.Services;

namespace CashPilot.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        var dataDirectory = FileSystem.AppDataDirectory;
        builder.Services.AddSingleton(new AppSettings());
        builder.Services.AddSingleton(new PendingQueue(Path.Combine(dataDirectory, "pending.json")));
        builder.Services.AddSingleton(new LookupsCache(Path.Combine(dataDirectory, "lookups.json")));
        builder.Services.AddSingleton<CashPilotClient>();
        builder.Services.AddSingleton<SyncService>();

        builder.Services.AddTransient<NewEntryPage>();
        builder.Services.AddTransient<RecentPage>();
        builder.Services.AddTransient<SettingsPage>();

        return builder.Build();
    }
}
