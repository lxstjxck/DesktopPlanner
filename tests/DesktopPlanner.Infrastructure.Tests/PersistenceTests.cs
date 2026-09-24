using DesktopPlanner.Application;
using DesktopPlanner.Domain;
using DesktopPlanner.Infrastructure;
using Microsoft.EntityFrameworkCore;
namespace DesktopPlanner.Infrastructure.Tests;
public sealed class PersistenceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "DesktopPlannerTests", Guid.NewGuid().ToString("N"));
    private SqlitePlannerStore NewStore() => new(Path.Combine(directory, "test.db"));
    [Fact] public async Task LayoutAndNotesSurviveNewStoreInstance()
    {
        using (var store = NewStore())
        {
            await store.InitializeAsync();
            await store.SaveNoteAsync("First\nSecond — тест");
            await store.SaveLayoutAsync(new WidgetLayout { WidgetType = WidgetType.Notes, X = -900, Y = 100, MonitorId = "left", Scale = 1.5, Opacity = .6, IsVisible = false, IsPositionLocked = true });
            await store.SaveLayoutAsync(new WidgetLayout { WidgetType = WidgetType.Todo });
        }
        using var reopened = NewStore(); await reopened.InitializeAsync();
        Assert.Equal("First\nSecond — тест", await reopened.GetNoteAsync());
        var layouts = await reopened.GetLayoutsAsync(); Assert.Equal(2, layouts.Count);
        var notes = layouts.Single(l => l.WidgetType == WidgetType.Notes);
        Assert.Equal(-900, notes.X); Assert.Equal(1.5, notes.Scale); Assert.False(notes.IsVisible); Assert.True(notes.IsPositionLocked);
        notes.X = -800; await reopened.SaveLayoutAsync(notes);
        Assert.Equal(2, (await reopened.GetLayoutsAsync()).Count);
    }
    [Fact] public async Task TaskLifecycleAndReorderPersist()
    {
        using var store = NewStore(); await store.InitializeAsync(); var service = new PlannerService(store);
        await service.AddAsync("  first  "); await service.AddAsync("second"); await service.AddAsync("  ");
        var tasks = await service.GetTasksAsync(); Assert.Equal(2, tasks.Count); Assert.Equal("first", tasks[0].Title);
        await service.ReorderAsync([tasks[1].Id, tasks[0].Id]);
        Assert.Equal("second", (await service.GetTasksAsync())[0].Title);
        await service.CompleteAsync(tasks[0]); Assert.True((await service.GetTasksAsync()).Single(t => t.Id == tasks[0].Id).IsCompleted);
        await service.RestoreAsync(tasks[0]); Assert.Null((await service.GetTasksAsync()).Single(t => t.Id == tasks[0].Id).CompletedAt);
        await service.DeleteAsync(tasks[0]); Assert.Single(await service.GetTasksAsync());
    }
    [Fact] public async Task CalendarMappingRoundTripsAndDeletionUnlinksTask()
    {
        using var store = NewStore(); await store.InitializeAsync();
        var task = new TaskItem { Title = "Calendar mapping" };
        var item = SchedulingService.Schedule(task, new DateTime(2026, 9, 21, 9, 15, 0));
        item.ExternalId = "remote.ics"; item.ETag = "etag-1"; item.SyncStatus = SyncStatus.PendingUpload;
        await using (var db = store.CreateContext()) { db.Events.Add(item); await db.SaveChangesAsync(); }
        await using (var db = store.CreateContext())
        {
            var saved = await db.Events.Include(e => e.Task).SingleAsync();
            Assert.Equal(item.Start, saved.Start); Assert.Equal(item.End, saved.End); Assert.Equal("etag-1", saved.ETag);
            Assert.Equal(task.Id, saved.Task!.Id); Assert.Equal(SyncStatus.PendingUpload, saved.SyncStatus);
            db.Events.Remove(saved); await db.SaveChangesAsync();
        }
        Assert.Null((await store.GetTasksAsync()).Single().CalendarEventId);
    }
    [Fact] public async Task InvalidReorderDoesNotPartiallyWrite()
    {
        using var store = NewStore(); await store.InitializeAsync();
        await store.SaveTaskAsync(new TaskItem { Title = "one" });
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveTaskOrderAsync([Guid.NewGuid()]));
        Assert.Equal(0, (await store.GetTasksAsync()).Single().Order);
    }
    [Fact] public async Task ConcurrentNoteWritesAreSerialized()
    {
        using var store = NewStore(); await store.InitializeAsync();
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => store.SaveNoteAsync(i.ToString())));
        Assert.Equal("19", await store.GetNoteAsync());
    }
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
