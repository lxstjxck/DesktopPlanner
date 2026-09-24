using System.Text.Json;
using DesktopPlanner.Application;
using DesktopPlanner.Domain;

namespace DesktopPlanner.Infrastructure;

public sealed record ICloudSettings(string UserName, string CalendarId, string CalendarName, string CredentialKey, DateTime? LastSyncAt = null);

public sealed class CalendarSyncService(SqlitePlannerStore store, ICredentialStore credentials) : ICalendarSyncService, IDisposable
{
    private const string SettingsKey = "ICloudCalendarSync.v1";
    private readonly SemaphoreSlim gate = new(1, 1);
    public string Status { get; private set; } = "iCloud не подключён";
    public event Action? StatusChanged;
    public event Action? DataChanged;
    public Task<ICloudSettings?> GetSettingsAsync() => ReadSettingsAsync();
    private async Task<ICloudSettings?> ReadSettingsAsync()
    {
        var text = await store.GetSettingAsync(SettingsKey);
        return string.IsNullOrEmpty(text) ? null : JsonSerializer.Deserialize<ICloudSettings>(text);
    }
    private void SetStatus(string status) { Status = status; StatusChanged?.Invoke(); }
    public async Task ConnectAsync(string userName, string password, RemoteCalendar calendar, CancellationToken ct)
    {
        ICloudCalendarProvider.ValidateUri(calendar.Id);
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password)) throw new ArgumentException("Введите Apple Account и пароль приложения.");
        await gate.WaitAsync(ct);
        try
        {
            var previous = await ReadSettingsAsync();
            var key = "DesktopPlanner/iCloud/" + Guid.NewGuid().ToString("N");
            await credentials.SetAsync(key, password, ct);
            try { await store.SaveSettingAsync(SettingsKey, JsonSerializer.Serialize(new ICloudSettings(userName.Trim(), calendar.Id, calendar.Name, key))); }
            catch { await credentials.DeleteAsync(key, CancellationToken.None); throw; }
            if (previous is not null)
            {
                try { await credentials.DeleteAsync(previous.CredentialKey, CancellationToken.None); }
                catch (System.ComponentModel.Win32Exception) { /* The new connection is already durable. */ }
            }
            SetStatus("iCloud подключён · ожидает синхронизации");
        }
        finally { gate.Release(); }
    }
    public async Task DisconnectAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var config = await ReadSettingsAsync();
            // Remove the secret first; if Windows rejects this, keep the configuration available to retry.
            if (config is not null) await credentials.DeleteAsync(config.CredentialKey, ct);
            await store.SaveSettingAsync(SettingsKey, ""); SetStatus("iCloud отключён · события сохранены на компьютере");
        }
        finally { gate.Release(); }
    }
    public async Task SyncAsync(CancellationToken cancellationToken)
    {
        if (!await gate.WaitAsync(0, cancellationToken)) return;
        try
        {
            var settings = await ReadSettingsAsync();
            if (settings is null) { SetStatus("iCloud не подключён"); return; }
            SetStatus("iCloud · синхронизация…");
            await Task.Run(async () =>
            {
                var password = await credentials.GetAsync(settings.CredentialKey, cancellationToken)
                    ?? throw new InvalidOperationException("Пароль приложения не найден. Подключите iCloud заново.");
                using var provider = new ICloudCalendarProvider(settings.UserName, password);
                await SynchronizeAsync(provider, settings.CalendarId, cancellationToken);
            }, cancellationToken);
            await store.SaveSettingAsync(SettingsKey, JsonSerializer.Serialize(settings with { LastSyncAt = DateTime.UtcNow }));
            if (!Status.Contains("конфликт", StringComparison.OrdinalIgnoreCase)) SetStatus($"iCloud · обновлено в {DateTime.Now:HH:mm}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { SetStatus("iCloud · синхронизация остановлена"); }
        catch (HttpRequestException) { SetStatus("iCloud · нет связи. Изменения сохранены, повтор через 2 минуты."); }
        catch (OperationCanceledException) { SetStatus("iCloud · сервер не ответил. Повтор через 2 минуты."); }
        catch (Exception ex)
        {
            // Do not log protocol bodies, credentials, account URLs or calendar contents.
            Serilog.Log.Warning("Calendar sync failed: {ErrorType}", ex.GetType().Name);
            SetStatus("iCloud · " + (ex is InvalidOperationException or CalendarConflictException ? ex.Message : "Ошибка обмена. Повторите подключение или синхронизацию."));
        }
        finally { gate.Release(); DataChanged?.Invoke(); }
    }
    // Kept separate from credentials/network construction to verify conflict and retry behavior offline.
    public async Task SynchronizeAsync(ICalendarProvider provider, string calendarId, CancellationToken ct)
    {
        var known = await store.GetSyncEventsAsync(calendarId);
        var changes = await provider.GetChangesAsync(calendarId, known.Where(e => e.ExternalId is not null).ToDictionary(e => e.ExternalId!, e => e.ETag), ct);
        ct.ThrowIfCancellationRequested();
        var conflicts = await store.ApplyRemoteAsync(calendarId, changes);
        var pending = await store.ClaimUploadsAsync(calendarId);
        foreach (var item in pending)
        {
            ct.ThrowIfCancellationRequested();
            if (item.SyncStatus == SyncStatus.PendingDelete)
            {
                if (item.ETag is not null) await provider.DeleteAsync(calendarId, item.ExternalId!, item.ETag, ct);
                // A tombstone with no ETag is known absent from the complete inventory above.
                else if (changes.RemoteIds.Contains(item.ExternalId!)) throw new CalendarConflictException();
                await store.AcknowledgeDeleteAsync(item);
            }
            else
            {
                var result = await provider.SaveAsync(item, item.ETag, ct);
                await store.AcknowledgeUploadAsync(item, result);
            }
        }
        if (conflicts > 0) SetStatus($"iCloud · конфликтов: {conflicts}. Сохранены локальные копии; при конфликте удаления оставлена версия iCloud.");
    }
    public async Task WhenIdleAsync() { await gate.WaitAsync(); gate.Release(); }
    public void Dispose() => gate.Dispose();
}
