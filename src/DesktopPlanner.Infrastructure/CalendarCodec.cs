using Ical.Net;
using Ical.Net.DataTypes;
using Event = DesktopPlanner.Domain.CalendarEvent;
using IcalEvent = Ical.Net.CalendarComponents.CalendarEvent;

namespace DesktopPlanner.Infrastructure;

// Retained only to display recurring events already stored by older versions.
public static class CalendarCodec
{
    private static Calendar Load(string text) => Calendar.Load(text)
        ?? throw new InvalidOperationException("Сохранённый календарь пуст.");

    private static DateTime Local(CalDateTime value) => DateTime.SpecifyKind(
        !value.HasTime || string.IsNullOrEmpty(value.TzId) ? value.Value : value.AsUtc.ToLocalTime(), DateTimeKind.Unspecified);

    public static IEnumerable<Event> Visible(Event item, DateTime from, DateTime to)
    {
        if (!item.IsReadOnly || item.RawICalendar is null)
        { if (item.Start < to && item.End > from) yield return item; yield break; }
        var calendar = Load(item.RawICalendar);
        // Expansion is limited to the requested view; never enumerate an infinite series.
        var count = 0;
        foreach (var occurrence in calendar.GetOccurrences(new CalDateTime(DateTime.SpecifyKind(from, DateTimeKind.Unspecified))))
        {
            var start = Local(occurrence.Period.StartTime);
            if (start >= to) yield break;
            if (++count > 10000) throw new InvalidOperationException("Слишком много повторов в выбранном диапазоне.");
            if (occurrence.Source is not IcalEvent source || source.Status == "CANCELLED") continue;
            var end = occurrence.Period.EndTime is { } finish ? Local(finish) : start.AddMinutes(15);
            if (end <= from) continue;
            yield return new Event { Id = item.Id, Title = source.Summary ?? item.Title, Description = source.Description ?? "",
                Location = source.Location ?? "", Start = start, End = end > start ? end : start.AddMinutes(15),
                IsAllDay = source.IsAllDay, IsReadOnly = true, ColorHex = item.ColorHex,
                CalendarId = item.CalendarId, ExternalId = item.ExternalId, SyncStatus = item.SyncStatus };
        }
    }
}
