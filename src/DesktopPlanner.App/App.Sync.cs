using System.Windows.Threading;
using DesktopPlanner.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace DesktopPlanner.App;

public partial class App
{
    private readonly DispatcherTimer syncTimer = new() { Interval = TimeSpan.FromMinutes(2) };
    private readonly CancellationTokenSource syncLifetime = new();
    private CalendarSyncService? syncService;
    private ICloudSettingsWindow? syncWindow;
    private void StartSynchronization()
    {
        syncService = services!.GetRequiredService<CalendarSyncService>();
        syncService.DataChanged += () => Dispatcher.BeginInvoke(async () =>
        { if (!exiting && !exitPending) await planner.Calendar.RefreshAfterSyncAsync(); });
        syncTimer.Tick += async (_, _) => { if (!exitPending) await syncService.SyncAsync(syncLifetime.Token); };
        syncTimer.Start(); _ = syncService.SyncAsync(syncLifetime.Token);
    }
    private void OpenSyncSettings()
    {
        if (exitPending || exiting) return;
        if (syncWindow is not null) { syncWindow.Activate(); return; }
        syncWindow = new ICloudSettingsWindow(services!.GetRequiredService<CalendarSyncService>(), glass);
        syncWindow.Closed += (_, _) => syncWindow = null;
        syncWindow.Show();
    }
    private async Task SyncFromTrayAsync()
    {
        if (syncService is null || exitPending || exiting) return;
        if (await syncService.GetSettingsAsync() is null) { OpenSyncSettings(); return; }
        await syncService.SyncAsync(syncLifetime.Token); tray?.Notify(syncService.Status);
    }
}
