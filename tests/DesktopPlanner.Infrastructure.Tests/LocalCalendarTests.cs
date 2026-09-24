using DesktopPlanner.Application;
using DesktopPlanner.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DesktopPlanner.Infrastructure.Tests;

public sealed class LocalCalendarTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "DesktopPlannerLocal", Guid.NewGuid().ToString("N"));
    private SqlitePlannerStore Create() => new(Path.Combine(directory, "planner.db"));

    [Fact]
    public async Task CompletionHidesScheduledTaskAcrossReopenAndRestoreKeepsItsSchedule()
    {
        var start = new DateTime(2026, 9, 21, 10, 0, 0);
        Guid eventId;
        using (var store = Create())
        {
            await store.InitializeAsync();
            var task = new TaskItem { Title = "Scheduled" };
            await store.SaveTaskAsync(task);
            await store.ScheduleAsync(CalendarSource.Task, task.Id, start);
            eventId = Assert.Single(await store.GetEventsAsync(start.Date, start.Date.AddDays(1))).Id;
            await new PlannerService(store).CompleteAsync(task);
            Assert.Empty(await store.GetEventsAsync(start.Date, start.Date.AddDays(1)));
        }
        using var reopened = Create(); await reopened.InitializeAsync();
        Assert.Empty(await reopened.GetEventsAsync(start.Date, start.Date.AddDays(1)));
        var saved = Assert.Single(await reopened.GetTasksAsync());
        Assert.True(saved.IsCompleted); Assert.Equal(eventId, saved.CalendarEventId);
        await new PlannerService(reopened).RestoreAsync(saved);
        var restored = Assert.Single(await reopened.GetEventsAsync(start.Date, start.Date.AddDays(1)));
        Assert.Equal(eventId, restored.Id); Assert.Equal(start, restored.Start);
    }

    [Theory]
    [InlineData(SyncStatus.Synced)]
    [InlineData(SyncStatus.PendingUpload)]
    [InlineData(SyncStatus.Conflict)]
    public async Task PreviouslyImportedEventCanBeEditedDeletedAndRestoredLocally(SyncStatus status)
    {
        var start = new DateTime(2026, 9, 21, 10, 0, 0);
        var item = new CalendarEvent { Title = "Imported", Start = start, End = start.AddHours(1),
            CalendarId = "old-calendar", ExternalId = "event.ics", ETag = "old-tag", SyncStatus = status };
        var task = new TaskItem { Title = "Linked task", CalendarEventId = item.Id,
            ScheduledStart = item.Start, ScheduledEnd = item.End, IsCompleted = true };
        using (var store = Create())
        {
            await store.InitializeAsync();
            await using (var db = store.CreateContext())
            {
                db.Events.Add(item); db.Tasks.Add(task); await db.SaveChangesAsync();
            }
        }
        using var reopened = Create();
        await reopened.InitializeAsync();
        Assert.Empty(await reopened.GetEventsAsync(start.Date, start.Date.AddDays(1)));
        await reopened.UpdateEventAsync(item.Id, new EventEdit("Local edit", "", "", start.AddHours(1), start.AddHours(2), false));
        await reopened.DeleteEventAsync(item.Id);
        await using (var db = reopened.CreateContext()) Assert.False(await db.Events.AnyAsync());
        var unlinked = Assert.Single(await reopened.GetTasksAsync());
        Assert.True(unlinked.IsCompleted); Assert.Null(unlinked.CalendarEventId); Assert.Null(unlinked.ScheduledStart);
        Assert.True(await reopened.UndoCalendarAsync());
        await using var context = reopened.CreateContext();
        var restored = await context.Events.AsNoTracking().SingleAsync();
        Assert.Equal("Local edit", restored.Title);
        Assert.Equal(item.Id, Assert.Single(await reopened.GetTasksAsync()).CalendarEventId);
        Assert.True(await reopened.UndoCalendarAsync());
        Assert.Equal("Imported", (await context.Events.AsNoTracking().SingleAsync()).Title);
        Assert.Empty(await reopened.GetEventsAsync(start.Date, start.Date.AddDays(1)));
    }

    [Fact]
    public async Task SavedRecurrencesKeepExceptionsAndPendingDeletionsStayHidden()
    {
        using var store = Create(); await store.InitializeAsync();
        var start = new DateTime(2026, 9, 21, 10, 0, 0);
        const string series = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Test//EN\r\nBEGIN:VEVENT\r\nUID:series\r\nDTSTAMP:20260101T000000Z\r\nDTSTART:20260921T100000\r\nDTEND:20260921T110000\r\nRRULE:FREQ=DAILY;COUNT=4\r\nEXDATE:20260922T100000\r\nSUMMARY:Repeat\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        await using (var db = store.CreateContext())
        {
            db.Events.Add(new CalendarEvent { Title = "Repeat", Start = start, End = start.AddHours(1),
                IsReadOnly = true, RawICalendar = series, SyncStatus = SyncStatus.Synced });
            db.Events.Add(new CalendarEvent { Title = "Already deleted", Start = start, End = start.AddHours(1),
                SyncStatus = SyncStatus.PendingDelete });
            await db.SaveChangesAsync();
        }
        var events = await store.GetEventsAsync(start.Date, start.Date.AddDays(7));
        Assert.Equal(new[] { 21, 23, 24 }, events.Select(e => e.Start.Day));
        Assert.All(events, e => Assert.True(e.IsReadOnly));
        Assert.Single(await store.GetEventsAsync(start.Date.AddDays(3), start.Date.AddDays(4)));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
