using DesktopPlanner.Application;
using DesktopPlanner.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DesktopPlanner.Infrastructure.Tests;

public sealed class ResetDataTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "DesktopPlannerReset", Guid.NewGuid().ToString("N"));
    private SqlitePlannerStore Create() => new(Path.Combine(directory, "planner.db"));

    private static async Task SeedAsync(SqlitePlannerStore store)
    {
        await store.InitializeAsync();
        var task = new TaskItem { Title = "Completed", IsCompleted = true };
        await store.SaveTaskAsync(task);
        await store.ScheduleAsync(CalendarSource.Task, task.Id, DateTime.Today);
        await store.SaveTaskAsync(new TaskItem { Title = "Active" });
        await store.AddInboxAsync(new UnscheduledEvent { Title = "Inbox" });
        await store.SaveNoteAsync("Notes");
        await store.SaveLayoutAsync(new WidgetLayout { WidgetType = WidgetType.Notes, IsVisible = false });
        await store.SaveSettingAsync("CalendarViewport", "saved preference");
        await using var db = store.CreateContext();
        db.Events.Add(new CalendarEvent { Title = "Outside visible week", Start = DateTime.Today.AddYears(1),
            End = DateTime.Today.AddYears(1).AddHours(1), IsReadOnly = true });
        db.Events.Add(new CalendarEvent { Title = "Old tombstone", SyncStatus = SyncStatus.PendingDelete });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task ResetClearsAllContentAndUndoButPreservesLayoutAfterReopen()
    {
        using (var store = Create())
        {
            await SeedAsync(store);
            var pendingNote = store.SaveNoteAsync("Pending save");
            await store.ResetDataAsync(); await pendingNote;
            Assert.False(await store.UndoCalendarAsync());
            await store.ResetDataAsync();
        }
        using var reopened = Create(); await reopened.InitializeAsync();
        Assert.Empty(await reopened.GetTasksAsync());
        Assert.Empty(await reopened.GetInboxAsync());
        Assert.Equal("", await reopened.GetNoteAsync());
        await using var db = reopened.CreateContext();
        Assert.Empty(await db.Events.ToListAsync());
        Assert.False(Assert.Single(await reopened.GetLayoutsAsync()).IsVisible);
        Assert.Equal("saved preference", await reopened.GetSettingAsync("CalendarViewport"));
    }

    [Fact]
    public async Task FailedResetRollsBackAllDeletesAndPreservesUndo()
    {
        using var store = Create(); await SeedAsync(store);
        await using (var db = store.CreateContext())
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER RejectReset BEFORE DELETE ON Notes BEGIN SELECT RAISE(ABORT, 'reset failed'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => store.ResetDataAsync());
        Assert.Equal(2, (await store.GetTasksAsync()).Count);
        Assert.Single(await store.GetInboxAsync());
        Assert.Equal("Notes", await store.GetNoteAsync());
        await using (var db = store.CreateContext()) Assert.Equal(3, await db.Events.CountAsync());
        Assert.True(await store.UndoCalendarAsync());
        Assert.Empty(await store.GetInboxAsync());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
