using DesktopPlanner.Domain;
namespace DesktopPlanner.Application;

public interface IPlannerStore
{
    Task InitializeAsync();
    Task ApplyLayoutPresetAsync(IReadOnlyList<WidgetLayout> layouts, string version, bool force = false);
    Task<List<UnscheduledEvent>> GetInboxAsync();
    Task AddInboxAsync(UnscheduledEvent item);
    Task DeleteInboxAsync(Guid id);
    Task<List<CalendarEvent>> GetEventsAsync(DateTime from, DateTime to);
    Task ScheduleAsync(CalendarSource source, Guid id, DateTime start);
    Task ResizeEventAsync(Guid id, DateTime end);
    Task DeleteEventAsync(Guid id);
    Task UpdateEventAsync(Guid id, EventEdit edit);
    Task SetEventColorAsync(Guid id, string? colorHex);
    Task<bool> UndoCalendarAsync();
    Task<string?> GetSettingAsync(string key);
    Task SaveSettingAsync(string key, string value);
    Task<List<TaskItem>> GetTasksAsync();
    Task SaveTaskAsync(TaskItem task);
    Task DeleteTaskAsync(Guid id);
    Task SaveTaskOrderAsync(IReadOnlyList<Guid> ids);
    Task<string> GetNoteAsync();
    Task SaveNoteAsync(string text);
    Task<List<WidgetLayout>> GetLayoutsAsync();
    Task SaveLayoutAsync(WidgetLayout layout);
}
public sealed record EventEdit(string Title, string Description, string Location, DateTime Start, DateTime End, bool IsAllDay);
public sealed record RemoteCalendar(string Id, string Name);
public sealed record CalendarChangeSet(IReadOnlyList<CalendarEvent> Events, IReadOnlyList<string> RemoteIds);
public interface ICalendarProvider
{
    Task<IReadOnlyList<RemoteCalendar>> DiscoverAsync(CalendarAccount account, CancellationToken cancellationToken);
    Task<CalendarChangeSet> GetChangesAsync(string calendarId, IReadOnlyDictionary<string, string?> knownETags, CancellationToken cancellationToken);
    Task<CalendarEvent> SaveAsync(CalendarEvent item, string? expectedETag, CancellationToken cancellationToken);
    Task DeleteAsync(string calendarId, string externalId, string? expectedETag, CancellationToken cancellationToken);
}
public interface ICalendarSyncService { Task SyncAsync(CancellationToken cancellationToken); }
public interface ICredentialStore
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken);
    Task SetAsync(string key, string secret, CancellationToken cancellationToken);
    Task DeleteAsync(string key, CancellationToken cancellationToken);
}


