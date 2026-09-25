using DesktopPlanner.Application;
using DesktopPlanner.Domain;
using DesktopPlanner.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace DesktopPlanner.Infrastructure.Tests;
public sealed class CalendarPersistenceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "DesktopPlannerTests", Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(directory, "test.db");
    private SqlitePlannerStore NewStore() => new(DbPath);
    [Fact] public async Task InboxConversionIsAtomicAndCannotBeRepeated()
    {
        using var store = NewStore(); await store.InitializeAsync();
        var inbox = new UnscheduledEvent { Title = "Doctor", Description = "Bring documents" }; await store.AddInboxAsync(inbox);
        var start = new DateTime(2026, 9, 21, 9, 15, 0);
        await store.ScheduleAsync(CalendarSource.Inbox, inbox.Id, start);
        Assert.Empty(await store.GetInboxAsync());
        var item = Assert.Single(await store.GetEventsAsync(start.Date, start.Date.AddDays(1)));
        Assert.Equal(inbox.Title, item.Title); Assert.Equal(inbox.Description, item.Description); Assert.Equal(start.AddHours(1), item.End);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ScheduleAsync(CalendarSource.Inbox, inbox.Id, start));
        Assert.Single(await store.GetEventsAsync(start.Date, start.Date.AddDays(1)));
    }
    [Fact] public async Task FailedInboxConversionKeepsOriginalRecord()
    {
        using var store = NewStore(); await store.InitializeAsync(); var inbox = new UnscheduledEvent { Title = "Retain me" };
        await store.AddInboxAsync(inbox);
        await using (var db = store.CreateContext())
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER FailEventInsert BEFORE INSERT ON Events BEGIN SELECT RAISE(ABORT, 'simulated disk failure'); END;");
        await Assert.ThrowsAsync<DbUpdateException>(() => store.ScheduleAsync(CalendarSource.Inbox, inbox.Id, DateTime.Today));
        Assert.Single(await store.GetInboxAsync()); Assert.Empty(await store.GetEventsAsync(DateTime.Today, DateTime.Today.AddDays(1)));
    }
    [Fact] public async Task TaskMoveResizeAndDeleteKeepLinkConsistentAfterReopen()
    {
        var start = new DateTime(2026, 9, 21, 23, 45, 0); var task = new TaskItem { Title = "Work" };
        Guid id;
        using (var store = NewStore())
        {
            await store.InitializeAsync(); await store.SaveTaskAsync(task);
            await store.ScheduleAsync(CalendarSource.Task, task.Id, start);
            id = (await store.GetTasksAsync()).Single().CalendarEventId!.Value;
            await store.ScheduleAsync(CalendarSource.Task, task.Id, start.AddDays(1));
            Assert.Equal(id, (await store.GetTasksAsync()).Single().CalendarEventId);
            await store.ScheduleAsync(CalendarSource.Event, id, start.AddDays(2));
            await store.ResizeEventAsync(id, start.AddDays(2).AddMinutes(90));
        }
        using var reopened = NewStore(); await reopened.InitializeAsync();
        var saved = Assert.Single(await reopened.GetTasksAsync());
        Assert.Equal(start.AddDays(2), saved.ScheduledStart); Assert.Equal(start.AddDays(2).AddMinutes(90), saved.ScheduledEnd);
        Assert.Single(await reopened.GetEventsAsync(start.AddDays(3).Date, start.AddDays(4).Date));
        await Assert.ThrowsAsync<ArgumentException>(() => reopened.ResizeEventAsync(id, saved.ScheduledStart!.Value));
        await reopened.DeleteEventAsync(id);
        saved = Assert.Single(await reopened.GetTasksAsync()); Assert.Null(saved.CalendarEventId); Assert.Null(saved.ScheduledStart); Assert.Null(saved.ScheduledEnd);
        Assert.Empty(await reopened.GetEventsAsync(start.Date, start.AddDays(5)));
    }
    [Fact] public async Task LegacyDatabaseIsAdoptedWithBackupWithoutLosingData()
    {
        Directory.CreateDirectory(directory);
        using var store = NewStore();
        await using (var db = store.CreateContext())
        {
            // Reproduce the first release's schema with no migration history.
            await db.GetService<IMigrator>().MigrateAsync(DatabaseMigrator.Baseline);
            await db.Database.ExecuteSqlRawAsync("DROP TABLE __EFMigrationsHistory;");
            db.Tasks.Add(new TaskItem { Title = "Preserved", IsCompleted = true });
            db.Notes.Add(new Note { Text = "Existing note" });
            await db.Database.ExecuteSqlRawAsync("INSERT INTO Layouts (WidgetType, X, Y, Width, Height, Scale, Opacity, MonitorId, IsVisible, IsPositionLocked) VALUES (4, -400, 60, 340, 360, 1, 0.94, '', 0, 0);");
            await db.SaveChangesAsync();
        }
        await store.InitializeAsync();
        Assert.Equal("Preserved", (await store.GetTasksAsync()).Single().Title);
        Assert.Equal("Existing note", await store.GetNoteAsync()); Assert.False((await store.GetLayoutsAsync()).Single().IsVisible);
        var backup = Assert.Single(Directory.GetFiles(directory, "*.backup-*.db"));
        await using (var connection = new SqliteConnection($"Data Source={backup}"))
        { await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "SELECT Text FROM Notes"; Assert.Equal("Existing note", await command.ExecuteScalarAsync()); }
        await using (var db = store.CreateContext()) Assert.Equal(6, (await db.Database.GetAppliedMigrationsAsync()).Count());
        await store.InitializeAsync(); Assert.Single(Directory.GetFiles(directory, "*.backup-*.db"));
    }
    [Fact] public async Task UnknownSchemaIsRejectedWithoutChangingRows()
    {
        Directory.CreateDirectory(directory);
        await using (var connection = new SqliteConnection($"Data Source={DbPath}"))
        {
            await connection.OpenAsync(); using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE Important (Text TEXT); INSERT INTO Important VALUES ('keep');"; await command.ExecuteNonQueryAsync();
        }
        using var store = NewStore(); await Assert.ThrowsAsync<InvalidOperationException>(store.InitializeAsync);
        await using var reopened = new SqliteConnection($"Data Source={DbPath}"); await reopened.OpenAsync();
        using var query = reopened.CreateCommand(); query.CommandText = "SELECT Text FROM Important"; Assert.Equal("keep", await query.ExecuteScalarAsync());
    }
    [Fact] public async Task FreshDatabaseHasCurrentMigrationAndNoPendingModelChanges()
    {
        using var store = NewStore(); await store.InitializeAsync();
        await using var db = store.CreateContext();
        Assert.Equal(6, (await db.Database.GetAppliedMigrationsAsync()).Count());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync()); Assert.False(db.Database.HasPendingModelChanges());
    }
    [Fact] public async Task StaleTodoCompletionDoesNotEraseCalendarSchedule()
    {
        using var store = NewStore(); await store.InitializeAsync();
        var task = new TaskItem { Title = "Schedule then complete" }; await store.SaveTaskAsync(task);
        var stale = (await store.GetTasksAsync()).Single();
        await store.ScheduleAsync(CalendarSource.Task, task.Id, DateTime.Today.AddHours(9));
        stale.Complete(DateTime.UtcNow); await store.SaveTaskAsync(stale);
        var saved = (await store.GetTasksAsync()).Single();
        Assert.True(saved.IsCompleted); Assert.NotNull(saved.CalendarEventId); Assert.Equal(DateTime.Today.AddHours(9), saved.ScheduledStart);
    }
    [Fact] public async Task LinkedTaskAndEventTitlesStaySynchronizedAfterReopen()
    {
        Guid eventId;
        using (var store = NewStore())
        {
            await store.InitializeAsync();
            var task = new TaskItem { Title = "Initial" }; await store.SaveTaskAsync(task);
            await store.ScheduleAsync(CalendarSource.Task, task.Id, new DateTime(2026, 9, 21, 9, 0, 0));
            eventId = (await store.GetTasksAsync()).Single().CalendarEventId!.Value;
            var renamed = (await store.GetTasksAsync()).Single(); renamed.Title = "From task";
            await store.SaveTaskAsync(renamed);
            Assert.Equal("From task", (await store.GetEventsAsync(new DateTime(2026, 9, 21), new DateTime(2026, 9, 22))).Single(e => e.Id == eventId).Title);
            await store.UpdateEventAsync(eventId, new EventEdit("From calendar", "", "", new DateTime(2026, 9, 21, 9, 0, 0), new DateTime(2026, 9, 21, 10, 0, 0), false));
        }
        using var reopened = NewStore(); await reopened.InitializeAsync();
        Assert.Equal("From calendar", (await reopened.GetTasksAsync()).Single().Title);
        Assert.Equal("From calendar", (await reopened.GetEventsAsync(new DateTime(2026, 9, 21), new DateTime(2026, 9, 22))).Single().Title);
    }
    [Fact] public async Task LayoutPresetIsAppliedOnceAndPreservesUserVisibility()
    {
        using var store = NewStore(); await store.InitializeAsync();
        await store.SaveLayoutAsync(new WidgetLayout { WidgetType = WidgetType.Week, Width = 340, IsVisible = false, IsPositionLocked = true });
        var monitor = new MonitorArea("primary", 0, 0, 1920, 1080, 1, true);
        await store.ApplyLayoutPresetAsync(ReferenceLayout.Create(monitor), ReferenceLayout.Version);
        var week = (await store.GetLayoutsAsync()).Single(l => l.WidgetType == WidgetType.Week);
        Assert.Equal(340, week.Width); Assert.False(week.IsVisible); Assert.True(week.IsPositionLocked);
        week.Width = 777; await store.SaveLayoutAsync(week);
        await store.ApplyLayoutPresetAsync(ReferenceLayout.Create(monitor), ReferenceLayout.Version);
        Assert.Equal(777, (await store.GetLayoutsAsync()).Single(l => l.WidgetType == WidgetType.Week).Width);
    }
    [Fact] public async Task NewPresetAddsTrackerWithoutMovingExistingWidgets()
    {
        using var store = NewStore(); await store.InitializeAsync();
        await store.SaveLayoutAsync(new WidgetLayout { WidgetType = WidgetType.Week, X = 777, Y = 222, Width = 500 });
        await store.SaveSettingAsync("LayoutPreset", ReferenceLayout.Version);
        var monitor = new MonitorArea("primary", 0, 0, 1920, 1080, 1, true);
        await store.ApplyLayoutPresetAsync(ReferenceLayout.Create(monitor), ReferenceLayout.Version);
        var layouts = await store.GetLayoutsAsync();
        var week = layouts.Single(l => l.WidgetType == WidgetType.Week);
        Assert.Equal(777, week.X); Assert.Equal(222, week.Y); Assert.Equal(500, week.Width);
        Assert.Contains(layouts, l => l.WidgetType == WidgetType.MonthTracker);
    }
    [Fact] public async Task TrackerLayoutsKeepTheirOwnNamesColorsAndAreLimitedToFour()
    {
        using var store = NewStore(); await store.InitializeAsync();
        for (var i = 0; i < 4; i++)
            await store.SaveLayoutAsync(new WidgetLayout { WidgetType = WidgetType.MonthTracker, TrackerId = $"tracker-{i}", TrackerTitle = $"Theme {i}", TrackerColorHex = "#5CC8FF" });
        var trackers = (await store.GetLayoutsAsync()).Where(layout => layout.WidgetType == WidgetType.MonthTracker).OrderBy(layout => layout.TrackerId).ToList();
        Assert.Equal(4, trackers.Count); Assert.Equal("Theme 2", trackers.Single(layout => layout.TrackerId == "tracker-2").TrackerTitle);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveLayoutAsync(new WidgetLayout { WidgetType = WidgetType.MonthTracker, TrackerId = "tracker-4" }));
    }
    [Fact] public async Task TrackerMigrationKeepsExistingMarksWithTheOriginalTracker()
    {
        Directory.CreateDirectory(directory);
        using var store = NewStore();
        await using (var db = store.CreateContext())
        {
            await db.GetService<IMigrator>().MigrateAsync("20260924120000_HabitDayMarks");
            await db.Database.ExecuteSqlRawAsync("INSERT INTO Layouts (WidgetType, X, Y, Width, Height, Scale, Opacity, MonitorId, IsVisible, IsPositionLocked) VALUES (5, 60, 60, 340, 360, 1, 0.94, '', 1, 0);");
            await db.Database.ExecuteSqlRawAsync("INSERT INTO HabitDayMarks (Date, ColorHex, UpdatedAt) VALUES ('2026-09-12 00:00:00', '#FF8FA3', '2026-09-12 00:00:00');");
        }
        await store.InitializeAsync();
        var tracker = Assert.Single(await store.GetLayoutsAsync(), layout => layout.WidgetType == WidgetType.MonthTracker);
        Assert.Equal(WidgetLayout.DefaultTrackerId, tracker.TrackerId); Assert.Equal("Трекер", tracker.TrackerTitle); Assert.Equal("#5CC8FF", tracker.TrackerColorHex);
        Assert.Equal("#FF8FA3", Assert.Single(await store.GetHabitDayMarksAsync(WidgetLayout.DefaultTrackerId, new DateTime(2026, 9, 1), new DateTime(2026, 10, 1))).ColorHex);
    }
    public void Dispose()
    { SqliteConnection.ClearAllPools(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}


