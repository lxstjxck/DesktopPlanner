using DesktopPlanner.Domain;
namespace DesktopPlanner.Application;

public enum CalendarSource { Task, Inbox, Event }
public sealed record CalendarDrag(CalendarSource Source, Guid Id, double GrabOffsetMinutes = 0);
public sealed class CalendarService(IPlannerStore store)
{
    public Task<List<UnscheduledEvent>> GetInboxAsync() => store.GetInboxAsync();
    public Task<List<CalendarEvent>> GetEventsAsync(DateTime from, DateTime to) => store.GetEventsAsync(from, to);
    public Task AddInboxAsync(string title, string description)
    {
        title = title.Trim();
        return title.Length == 0 ? Task.CompletedTask : store.AddInboxAsync(new UnscheduledEvent { Title = title, Description = description.Trim() });
    }
    public Task DeleteInboxAsync(Guid id) => store.DeleteInboxAsync(id);
    public Task ScheduleAsync(CalendarDrag source, DateTime start) => store.ScheduleAsync(source.Source, source.Id, SchedulingService.Snap(start, 30));
    public Task ResizeAsync(Guid id, DateTime end) => store.ResizeEventAsync(id, SchedulingService.Snap(end, 30));
    public Task DeleteEventAsync(Guid id) => store.DeleteEventAsync(id);
    public Task UpdateAsync(Guid id, EventEdit edit) => store.UpdateEventAsync(id, edit);
    public Task SetColorAsync(Guid id, string? colorHex) => store.SetEventColorAsync(id, colorHex);
    public Task<bool> UndoAsync() => store.UndoCalendarAsync();
    public Task<string?> GetViewportAsync() => store.GetSettingAsync("CalendarViewport");
    public Task SaveViewportAsync(string value) => store.SaveSettingAsync("CalendarViewport", value);
}

public static class WeekGeometry
{
    public const double HourHeight = 60;
    public const double DayHeight = 24 * HourHeight;
    public const double TimeGutter = 46;
    public const double MinimumDayWidth = 90;
    public static DateTime Monday(DateTime day) => day.Date.AddDays(-((int)day.DayOfWeek + 6) % 7);
    public static DateTime TimeAt(DateTime monday, double x, double y, double dayWidth)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(dayWidth) || dayWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(dayWidth));
        var day = Math.Clamp((int)Math.Floor(x / dayWidth), 0, 6);
        var minute = Math.Clamp(y / HourHeight * 60, 0, 1439);
        return SchedulingService.Snap(monday.Date.AddDays(day).AddMinutes(minute));
    }
    // Split overnight events at midnight; assign columns to overlapping intervals.
    public static IReadOnlyList<EventSegment> Arrange(IEnumerable<CalendarEvent> events, DateTime monday)
    {
        var result = new List<EventSegment>();
        for (var day = 0; day < 7; day++)
        {
            var start = monday.Date.AddDays(day); var end = start.AddDays(1);
            var segments = events.Where(e => e.Start < end && e.End > start && e.End > e.Start)
                .Select(e => new EventSegment(e, day, (e.Start > start ? e.Start : start), (e.End < end ? e.End : end)))
                .OrderBy(s => s.Start).ThenByDescending(s => s.End).ToList();
            var group = new List<EventSegment>(); var groupEnd = DateTime.MinValue;
            foreach (var segment in segments)
            {
                if (segment.Start >= groupEnd) { Assign(group); group.Clear(); }
                group.Add(segment); if (segment.End > groupEnd) groupEnd = segment.End;
            }
            Assign(group); result.AddRange(segments);
        }
        return result;
    }
    private static void Assign(List<EventSegment> group)
    {
        var ends = new List<DateTime>();
        foreach (var segment in group)
        {
            var column = ends.FindIndex(end => end <= segment.Start);
            if (column < 0) { column = ends.Count; ends.Add(segment.End); } else ends[column] = segment.End;
            segment.Column = column;
        }
        foreach (var segment in group) segment.ColumnCount = ends.Count;
    }
}
public sealed class EventSegment(CalendarEvent item, int day, DateTime start, DateTime end)
{
    public CalendarEvent Event { get; } = item;
    public int Day { get; } = day;
    public DateTime Start { get; } = start;
    public DateTime End { get; } = end;
    public int Column { get; set; }
    public int ColumnCount { get; set; } = 1;
    public double Top => Start.TimeOfDay.TotalHours * WeekGeometry.HourHeight;
    public double Height => (End - Start).TotalHours * WeekGeometry.HourHeight;
    public bool CanResize => End == Event.End && !Event.IsAllDay;
}
