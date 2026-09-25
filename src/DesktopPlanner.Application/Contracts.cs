using DesktopPlanner.Domain;
namespace DesktopPlanner.Application;

public interface IPlannerStore
{
    Task InitializeAsync();
    Task ResetDataAsync();
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
    Task<List<HabitDayMark>> GetHabitDayMarksAsync(string trackerId, DateTime from, DateTime to);
    Task SaveHabitDayMarkAsync(HabitDayMark mark);
    Task DeleteHabitDayMarkAsync(string trackerId, DateTime date);
}
public sealed record EventEdit(string Title, string Description, string Location, DateTime Start, DateTime End, bool IsAllDay);
