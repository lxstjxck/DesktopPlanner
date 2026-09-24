using DesktopPlanner.Domain;
namespace DesktopPlanner.Application;

public sealed class PlannerService(IPlannerStore store)
{
    public Task<List<TaskItem>> GetTasksAsync() => store.GetTasksAsync();
    public async Task AddAsync(string title)
    {
        title = title.Trim();
        if (title.Length == 0) return;
        var tasks = await store.GetTasksAsync();
        await store.SaveTaskAsync(new TaskItem { Title = title, Order = tasks.Count == 0 ? 0 : tasks.Max(t => t.Order) + 1 });
    }
    public async Task CompleteAsync(TaskItem task)
    { task.Complete(DateTime.UtcNow); await store.SaveTaskAsync(task); }
    public async Task RestoreAsync(TaskItem task)
    { task.Restore(DateTime.UtcNow); await store.SaveTaskAsync(task); }
    public Task DeleteAsync(TaskItem task) => store.DeleteTaskAsync(task.Id);
    public Task ReorderAsync(IReadOnlyList<Guid> ids) => store.SaveTaskOrderAsync(ids);
    public Task<string> GetNoteAsync() => store.GetNoteAsync();
    public Task SaveNoteAsync(string text) => store.SaveNoteAsync(text);
}
public static class SchedulingService
{
    public static DateTime Snap(DateTime time, int minutes = 15)
    {
        if (minutes <= 0 || 60 % minutes != 0) throw new ArgumentOutOfRangeException(nameof(minutes));
        var interval = TimeSpan.FromMinutes(minutes).Ticks;
        return new DateTime(time.Ticks / interval * interval, time.Kind);
    }
    public static CalendarEvent Schedule(TaskItem task, DateTime start, TimeSpan? duration = null, CalendarEvent? existing = null)
    {
        var length = duration ?? TimeSpan.FromHours(1);
        if (length <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        if (task.CalendarEventId.HasValue && existing?.Id != task.CalendarEventId)
            throw new InvalidOperationException("Supply the linked event when rescheduling.");
        var item = existing ?? new CalendarEvent();
        item.Title = task.Title; item.SetPeriod(Snap(start), Snap(start).Add(length));
        item.Task = task; task.CalendarEventId = item.Id;
        task.ScheduledStart = item.Start; task.ScheduledEnd = item.End; task.UpdatedAt = DateTime.UtcNow;
        return item;
    }
}
