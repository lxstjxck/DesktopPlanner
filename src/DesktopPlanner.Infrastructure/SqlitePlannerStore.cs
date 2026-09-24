using DesktopPlanner.Application;
using DesktopPlanner.Domain;
using Microsoft.EntityFrameworkCore;

namespace DesktopPlanner.Infrastructure;

public sealed class PlannerDbContext(DbContextOptions<PlannerDbContext> options) : DbContext(options)
{
    public DbSet<TaskItem> Tasks => Set<TaskItem>();
    public DbSet<CalendarEvent> Events => Set<CalendarEvent>();
    public DbSet<WidgetLayout> Layouts => Set<WidgetLayout>();
    public DbSet<Note> Notes => Set<Note>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<TaskItem>().HasOne<CalendarEvent>().WithOne(e => e.Task)
            .HasForeignKey<TaskItem>(t => t.CalendarEventId).OnDelete(DeleteBehavior.SetNull);
        model.Entity<TaskItem>().Property(t => t.Title).IsRequired();
        model.Entity<WidgetLayout>().HasIndex(l => l.WidgetType).IsUnique();
        model.Entity<UnscheduledEvent>();
        model.Entity<CalendarEvent>().HasIndex(e => new { e.Start, e.End });
        model.Entity<CalendarEvent>().HasIndex(e => new { e.CalendarId, e.ExternalId }).IsUnique();
        model.Entity<AppSetting>().HasKey(s => s.Key);
        model.Entity<CalendarAccount>();
        model.Entity<SyncMetadata>().HasOne<CalendarAccount>().WithMany().HasForeignKey(s => s.CalendarAccountId);
    }
}

// A fresh context per operation; SQLite work runs off the dispatcher and writes are serialized.
public sealed partial class SqlitePlannerStore(string path) : IPlannerStore, IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public PlannerDbContext CreateContext() => new(new DbContextOptionsBuilder<PlannerDbContext>()
        .UseSqlite(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true }.ToString()).Options);
    private async Task<T> Run<T>(Func<PlannerDbContext, Task<T>> action)
    {
        await gate.WaitAsync();
        try { return await Task.Run(async () => { await using var db = CreateContext(); return await action(db); }); }
        finally { gate.Release(); }
    }
    public Task InitializeAsync() => Run(async db =>
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await DatabaseMigrator.UpgradeAsync(db, path);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
        return true;
    });
    public Task ResetDataAsync() => Run(async db =>
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Tasks.ExecuteDeleteAsync();
        await db.Events.ExecuteDeleteAsync();
        await db.Set<UnscheduledEvent>().ExecuteDeleteAsync();
        await db.Notes.ExecuteDeleteAsync();
        await transaction.CommitAsync();
        calendarUndo.Clear();
        return true;
    });
    public Task<List<TaskItem>> GetTasksAsync() => Run(db => db.Tasks.AsNoTracking().OrderBy(t => t.Order).ToListAsync());
    public Task SaveTaskAsync(TaskItem task) => Run(async db =>
    {
        var current = await db.Tasks.FindAsync(task.Id);
        if (current is null) db.Tasks.Add(task);
        else
        {
            // A Todo action may hold a snapshot from before a concurrent calendar operation.
            // Scheduling and order are owned by their dedicated transactional operations.
            current.Title = task.Title; current.IsCompleted = task.IsCompleted;
            current.CompletedAt = task.CompletedAt; current.UpdatedAt = task.UpdatedAt;
            if (current.CalendarEventId is Guid eventId)
            {
                var linkedEvent = await db.Events.FindAsync(eventId);
                if (linkedEvent is not null) linkedEvent.Title = task.Title;
            }
        }
        await db.SaveChangesAsync(); return true;
    });
    public Task DeleteTaskAsync(Guid id) => Run(async db => { await db.Tasks.Where(t => t.Id == id).ExecuteDeleteAsync(); return true; });
    public Task SaveTaskOrderAsync(IReadOnlyList<Guid> ids) => Run(async db =>
    {
        var tasks = await db.Tasks.Where(t => !t.IsCompleted).ToDictionaryAsync(t => t.Id);
        if (ids.Count != tasks.Count || ids.Distinct().Count() != ids.Count || ids.Any(id => !tasks.ContainsKey(id)))
            throw new ArgumentException("Order must contain every active task exactly once.");
        for (var i = 0; i < ids.Count; i++) tasks[ids[i]].Order = i;
        await db.SaveChangesAsync(); return true;
    });
    public Task<string> GetNoteAsync() => Run(async db => (await db.Notes.FindAsync(1))?.Text ?? "");
    public Task SaveNoteAsync(string text) => Run(async db =>
    {
        var note = await db.Notes.FindAsync(1);
        if (note is null) db.Notes.Add(new Note { Text = text }); else note.Text = text;
        await db.SaveChangesAsync(); return true;
    });
    public Task<List<WidgetLayout>> GetLayoutsAsync() => Run(db => db.Layouts.AsNoTracking().ToListAsync());
    public Task SaveLayoutAsync(WidgetLayout layout)
    {
        layout.Validate();
        // Snapshot now so moving a window cannot mutate an in-flight write.
        var copy = new WidgetLayout { Id = layout.Id, WidgetType = layout.WidgetType, X = layout.X, Y = layout.Y,
            Width = layout.Width, Height = layout.Height, Scale = layout.Scale, Opacity = layout.Opacity,
            MonitorId = layout.MonitorId, IsVisible = layout.IsVisible, IsPositionLocked = layout.IsPositionLocked };
        return Run(async db =>
        {
            var current = await db.Layouts.SingleOrDefaultAsync(l => l.WidgetType == copy.WidgetType);
            if (current is null) { copy.Id = 0; db.Layouts.Add(copy); }
            else { copy.Id = current.Id; db.Entry(current).CurrentValues.SetValues(copy); }
            await db.SaveChangesAsync(); return true;
        });
    }
    public Task<List<UnscheduledEvent>> GetInboxAsync() => Run(db => db.Set<UnscheduledEvent>().AsNoTracking().OrderBy(e => e.CreatedAt).ToListAsync());
    public Task AddInboxAsync(UnscheduledEvent item) => Run(async db => { db.Add(item); await SaveCalendarAsync(db); return true; });
    public Task DeleteInboxAsync(Guid id) => Run(async db => { var item = await db.Set<UnscheduledEvent>().FindAsync(id); if (item is null) return false; db.Remove(item); await SaveCalendarAsync(db); return true; });
    public Task<List<CalendarEvent>> GetEventsAsync(DateTime from, DateTime to) => Run(async db =>
    {
        // Legacy tombstones must stay hidden even though synchronization no longer runs.
        var items = await db.Events.AsNoTracking().Where(e => ((e.Start < to && e.End > from) || e.IsReadOnly)
            && e.SyncStatus != SyncStatus.PendingDelete && (e.Task == null || !e.Task.IsCompleted)).ToListAsync();
        return items.SelectMany(e => CalendarCodec.Visible(e, from, to)).OrderBy(e => e.Start).ToList();
    });
    public Task ScheduleAsync(CalendarSource source, Guid id, DateTime start) => Run(async db =>
    {
        CalendarEvent item;
        switch (source)
        {
            case CalendarSource.Task:
                var task = await db.Tasks.FindAsync(id) ?? throw new InvalidOperationException("Задача уже удалена.");
                var existing = task.CalendarEventId is Guid eventId ? await db.Events.FindAsync(eventId) : null;
                if (existing is not null) EnsureEditable(existing);
                item = SchedulingService.Schedule(task, start, existing is null ? null : existing.End - existing.Start, existing);
                if (existing is null) db.Events.Add(item);
                break;
            case CalendarSource.Inbox:
                var inbox = await db.Set<UnscheduledEvent>().FindAsync(id) ?? throw new InvalidOperationException("Событие уже запланировано или удалено.");
                item = new CalendarEvent { Title = inbox.Title, Description = inbox.Description };
                item.SetPeriod(start, start.AddHours(1)); db.Events.Add(item); db.Remove(inbox);
                break;
            case CalendarSource.Event:
                item = await db.Events.Include(e => e.Task).SingleOrDefaultAsync(e => e.Id == id)
                    ?? throw new InvalidOperationException("Событие уже удалено.");
                if (item.IsAllDay) throw new InvalidOperationException("Перенос событий на весь день пока не поддерживается.");
                EnsureEditable(item);
                item.SetPeriod(start, start.Add(item.End - item.Start));
                UpdateLinkedTask(item);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(source));
        }
        await SaveCalendarAsync(db); return true;
    });
    public Task ResizeEventAsync(Guid id, DateTime end) => Run(async db =>
    {
        var item = await db.Events.Include(e => e.Task).SingleOrDefaultAsync(e => e.Id == id)
            ?? throw new InvalidOperationException("Событие уже удалено.");
        if (item.IsAllDay) throw new InvalidOperationException("Размер события на весь день нельзя менять здесь.");
        EnsureEditable(item);
        if (end - item.Start < TimeSpan.FromMinutes(30)) throw new ArgumentException("Минимальная длительность — 30 минут.");
        item.SetPeriod(item.Start, end); UpdateLinkedTask(item);
        await SaveCalendarAsync(db); return true;
    });
    public Task DeleteEventAsync(Guid id) => Run(async db =>
    {
        var item = await db.Events.Include(e => e.Task).SingleOrDefaultAsync(e => e.Id == id);
        if (item is null) return false;
        EnsureEditable(item);
        if (item.Task is { } task)
        { task.CalendarEventId = null; task.ScheduledStart = null; task.ScheduledEnd = null; task.UpdatedAt = DateTime.UtcNow; }
        db.Events.Remove(item);
        await SaveCalendarAsync(db); return true;
    });
    private static void UpdateLinkedTask(CalendarEvent item)
    {
        if (item.Task is not { } task) return;
        task.Title = item.Title;
        task.ScheduledStart = item.Start; task.ScheduledEnd = item.End; task.UpdatedAt = DateTime.UtcNow;
    }
    private static void EnsureEditable(CalendarEvent item)
    {
        if (item.IsReadOnly) throw new InvalidOperationException("Редактирование повторов и приглашений пока не поддерживается.");
        if (item.SyncStatus == SyncStatus.PendingDelete) throw new InvalidOperationException("Событие уже удалено.");
    }
    public Task ApplyLayoutPresetAsync(IReadOnlyList<WidgetLayout> layouts, string version, bool force = false) => Run(async db =>
    {
        var setting = await db.Set<AppSetting>().FindAsync("LayoutPreset");
        if (!force && setting?.Value == version) return false;
        await using var transaction = await db.Database.BeginTransactionAsync();
        foreach (var layout in layouts)
        {
            layout.Validate();
            var current = await db.Layouts.SingleOrDefaultAsync(l => l.WidgetType == layout.WidgetType);
            if (current is null) db.Layouts.Add(layout);
            else
            {
                current.X = layout.X; current.Y = layout.Y; current.Width = layout.Width; current.Height = layout.Height;
                current.Scale = layout.Scale; current.Opacity = layout.Opacity; current.MonitorId = layout.MonitorId;
            }
        }
        if (setting is null) db.Add(new AppSetting { Key = "LayoutPreset", Value = version }); else setting.Value = version;
        await db.SaveChangesAsync(); await transaction.CommitAsync(); return true;
    });
    public void Dispose() => gate.Dispose();
}



