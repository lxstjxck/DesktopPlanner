using Ical.Net;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using Event = DesktopPlanner.Domain.CalendarEvent;
using IcalEvent = Ical.Net.CalendarComponents.CalendarEvent;

namespace DesktopPlanner.Infrastructure;

public static class CalendarCodec
{
    private static Calendar Load(string text) => Calendar.Load(text)
        ?? throw new InvalidOperationException("Сервер вернул пустой календарь.");

    public static Event Read(string text)
    {
        var calendar = Load(text);
        var source = calendar.Events.FirstOrDefault(e => e.RecurrenceIdentifier is null) ?? calendar.Events.FirstOrDefault()
            ?? throw new InvalidOperationException("В ресурсе календаря нет события.");
        if (source.DtStart is null || string.IsNullOrWhiteSpace(source.Uid))
            throw new InvalidOperationException("У события отсутствует дата или UID.");
        var start = Local(source.DtStart);
        var end = source.DtEnd is { } finish ? Local(finish) : start.Add(source.IsAllDay ? TimeSpan.FromDays(1) : TimeSpan.FromMinutes(15));
        return new Event
        {
            Title = source.Summary ?? "Без названия", Description = source.Description ?? "", Location = source.Location ?? "",
            Start = start, End = end > start ? end : start.AddMinutes(15), IsAllDay = source.IsAllDay,
            RawICalendar = text, IsReadOnly = calendar.Events.Count != 1 || source.RecurrenceRule is not null
                || source.Properties.ContainsKey("RDATE") || source.RecurrenceIdentifier is not null || source.Organizer is not null
                || source.Attendees.Count > 0 || source.Status == "CANCELLED",
            SyncStatus = Domain.SyncStatus.Synced
        };
    }

    public static string Write(Event item)
    {
        if (item.IsReadOnly) throw new InvalidOperationException("Повторы и приглашения редактируются в iCloud.");
        var calendar = item.RawICalendar is null ? new Calendar() : Load(item.RawICalendar);
        var source = calendar.Events.FirstOrDefault();
        if (source is null) { source = new IcalEvent { Uid = item.Id.ToString("N") + "@desktopplanner" }; calendar.Events.Add(source); }
        source.Summary = item.Title; source.Description = item.Description; source.Location = item.Location;
        source.Properties.Remove("DURATION");
        source.DtStart = ToCalendarTime(item.Start, item.IsAllDay);
        source.DtEnd = ToCalendarTime(item.End, item.IsAllDay);
        source.DtStamp = CalDateTime.UtcNow; source.LastModified = CalDateTime.UtcNow;
        source.Sequence++;
        return new CalendarSerializer().SerializeToString(calendar)
            ?? throw new InvalidOperationException("Не удалось сформировать событие.");
    }

    private static CalDateTime ToCalendarTime(DateTime time, bool allDay)
    {
        var local = DateTime.SpecifyKind(time, DateTimeKind.Unspecified);
        if (allDay) return new CalDateTime(local.Date, false);
        if (TimeZoneInfo.Local.IsInvalidTime(local)) throw new InvalidOperationException("Это время пропущено при переходе на летнее время. Выберите другое.");
        return new CalDateTime(TimeZoneInfo.ConvertTimeToUtc(local));
    }

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
