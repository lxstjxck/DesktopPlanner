using DesktopPlanner.Application;
using DesktopPlanner.Domain;
using DesktopPlanner.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace DesktopPlanner.Infrastructure.Tests;

public sealed class CalendarUndoTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "DesktopPlannerUndo", Guid.NewGuid().ToString("N"));
    private SqlitePlannerStore Create() => new(Path.Combine(directory, "test.db"));
    [Fact]
    public async Task EventColorUpgradesExistingDatabaseAndSurvivesMoveUndoAndReopen()
    {
        Directory.CreateDirectory(directory);
        Guid id;
        using (var store = Create())
        {
            await using (var old = store.CreateContext())
                await old.GetService<IMigrator>().MigrateAsync("20260920213512_CalendarRangeIndex");
            await store.InitializeAsync();
            var task = new TaskItem { Title = "Colored task" }; await store.SaveTaskAsync(task);
            await store.ScheduleAsync(CalendarSource.Task, task.Id, DateTime.Today.AddHours(9));
            id = Assert.Single(await store.GetEventsAsync(DateTime.Today, DateTime.Today.AddDays(1))).Id;
            await store.SetEventColorAsync(id, "#467cd1");
            await store.ScheduleAsync(CalendarSource.Event, id, DateTime.Today.AddHours(10));
            await store.SetEventColorAsync(id, null);
            Assert.True(await store.UndoCalendarAsync());
            await using var db = store.CreateContext();
            Assert.False(db.Database.HasPendingModelChanges());
            Assert.NotEmpty(Directory.GetFiles(directory, "*.backup-*.db"));
        }
        using var reopened = Create(); await reopened.InitializeAsync();
        var item = Assert.Single(await reopened.GetEventsAsync(DateTime.Today, DateTime.Today.AddDays(1)));
        Assert.Equal(id, item.Id); Assert.Equal("#467CD1", item.ColorHex); Assert.Equal(10, item.Start.Hour);
    }
    [Fact]
    public async Task DeleteUndoRestoresEventAndScheduleWithoutRevertingTaskCompletion()
    {
        using var store = Create(); await store.InitializeAsync();
        var task = new TaskItem { Title = "Work" }; await store.SaveTaskAsync(task);
        await store.ScheduleAsync(CalendarSource.Task, task.Id, DateTime.Today.AddHours(9));
        var item = Assert.Single(await store.GetEventsAsync(DateTime.Today, DateTime.Today.AddDays(1)));
        await store.DeleteEventAsync(item.Id);
        var changed = Assert.Single(await store.GetTasksAsync()); changed.Complete(DateTime.UtcNow); changed.Title = "Renamed";
        await store.SaveTaskAsync(changed);
        Assert.True(await store.UndoCalendarAsync());
        Assert.Empty(await store.GetEventsAsync(DateTime.Today, DateTime.Today.AddDays(1)));
        await using var db = store.CreateContext();
        var restored = await db.Events.SingleAsync();
        var linked = Assert.Single(await store.GetTasksAsync());
        Assert.Equal(item.Id, restored.Id); Assert.Equal(item.Id, linked.CalendarEventId);
        Assert.True(linked.IsCompleted); Assert.Equal("Renamed", linked.Title); Assert.Equal(restored.End, linked.ScheduledEnd);
    }
    [Fact]
    public async Task UndoEditMoveAndInboxConversionRestoresOriginalRecords()
    {
        using var store = Create(); await store.InitializeAsync();
        var inbox = new UnscheduledEvent { Title = "Meeting", Description = "Details" }; await store.AddInboxAsync(inbox);
        var start = DateTime.Today.AddHours(9);
        await store.ScheduleAsync(CalendarSource.Inbox, inbox.Id, start);
        var item = Assert.Single(await store.GetEventsAsync(DateTime.Today, DateTime.Today.AddDays(1)));
        await store.ScheduleAsync(CalendarSource.Event, item.Id, start.AddHours(2));
        await store.UpdateEventAsync(item.Id, new EventEdit("Edited", "New", "Office", start.AddHours(2), start.AddHours(4), false));
        Assert.True(await store.UndoCalendarAsync());
        var moved = Assert.Single(await store.GetEventsAsync(DateTime.Today, DateTime.Today.AddDays(1)));
        Assert.Equal("Meeting", moved.Title); Assert.Equal(start.AddHours(2), moved.Start);
        Assert.True(await store.UndoCalendarAsync());
        Assert.Equal(start, Assert.Single(await store.GetEventsAsync(DateTime.Today, DateTime.Today.AddDays(1))).Start);
        Assert.True(await store.UndoCalendarAsync());
        Assert.Empty(await store.GetEventsAsync(DateTime.Today, DateTime.Today.AddDays(1)));
        Assert.Equal(inbox.Id, Assert.Single(await store.GetInboxAsync()).Id);
    }
    [Fact]
    public async Task FailedEditDoesNotAddUndoAndViewportSurvivesReopen()
    {
        using (var store = Create())
        {
            await store.InitializeAsync();
            var inbox = new UnscheduledEvent { Title = "Keep" }; await store.AddInboxAsync(inbox);
            var start = DateTime.Today.AddHours(9); await store.ScheduleAsync(CalendarSource.Inbox, inbox.Id, start);
            var item = Assert.Single(await store.GetEventsAsync(DateTime.Today, DateTime.Today.AddDays(1)));
            await Assert.ThrowsAsync<ArgumentException>(() => store.UpdateEventAsync(item.Id, new EventEdit("Bad", "", "", start, start, false)));
            Assert.True(await store.UndoCalendarAsync()); Assert.Equal(inbox.Id, Assert.Single(await store.GetInboxAsync()).Id);
            await store.SaveSettingAsync("CalendarViewport", "{\"Hour\":11.5,\"Detailed\":true}");
        }
        using var reopened = Create(); await reopened.InitializeAsync();
        Assert.Equal("{\"Hour\":11.5,\"Detailed\":true}", await reopened.GetSettingAsync("CalendarViewport"));
        Assert.False(await reopened.UndoCalendarAsync());
    }
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
