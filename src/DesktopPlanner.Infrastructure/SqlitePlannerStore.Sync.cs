using DesktopPlanner.Application;
using DesktopPlanner.Domain;
using Microsoft.EntityFrameworkCore;

namespace DesktopPlanner.Infrastructure;

public sealed partial class SqlitePlannerStore
{
    public Task<List<CalendarEvent>> GetSyncEventsAsync(string calendarId) => Run(db => db.Events.AsNoTracking()
        .Where(e => e.CalendarId == calendarId).ToListAsync());

    public Task<int> ApplyRemoteAsync(string calendarId, CalendarChangeSet changes) => Run(async db =>
    {
        var existing = await db.Events.Include(e => e.Task).Where(e => e.CalendarId == calendarId).ToListAsync();
        var byId = existing.Where(e => e.ExternalId is not null).ToDictionary(e => e.ExternalId!);
        var backfillKey = "ICloudTaskBackfill.v1:" + calendarId;
        var needsBackfill = await db.Set<AppSetting>().FindAsync(backfillKey) is null;
        var nextOrder = (await db.Tasks.MaxAsync(t => (int?)t.Order) ?? -1) + 1;
        void AddLinkedTask(CalendarEvent item)
        {
            if (item.Task is not null || item.SyncStatus == SyncStatus.PendingDelete) return;
            var task = new TaskItem { Title = item.Title, CalendarEventId = item.Id,
                ScheduledStart = item.Start, ScheduledEnd = item.End, Order = nextOrder++ };
            item.Task = task; db.Tasks.Add(task);
        }
        var conflicts = 0;
        foreach (var remote in changes.Events)
        {
            if (remote.ExternalId is null || remote.CalendarId != calendarId) throw new InvalidOperationException("Неверный адрес события при синхронизации.");
            if (!byId.TryGetValue(remote.ExternalId, out var local))
            { db.Events.Add(remote); AddLinkedTask(remote); continue; }
            if (local.ETag == remote.ETag) continue;
            var previousTitle = local.Title;
            var hadTask = local.Task is not null;
            // A PUT may have succeeded before the connection/app stopped: matching content acknowledges it.
            if (local.SyncStatus != SyncStatus.Synced && !SameContent(local, remote))
            {
                if (local.SyncStatus != SyncStatus.PendingDelete) PreserveConflict(db, local);
                conflicts++;
            }
            // A tombstone without an ETag may be an interrupted create followed by local deletion.
            var keepDelete = local.SyncStatus == SyncStatus.PendingDelete && local.ETag is null;
            CopyRemote(remote, local); if (keepDelete) local.SyncStatus = SyncStatus.PendingDelete;
            // Follow remote renames without overwriting an independently renamed local task.
            if (local.Task is { } linked && linked.Title == previousTitle) linked.Title = local.Title;
            // A conflict moves the original task to the local copy; the remote version gets its own.
            if (hadTask && local.Task is null) AddLinkedTask(local);
            UpdateLinkedTask(local);
        }
        var remoteIds = changes.RemoteIds.ToHashSet(StringComparer.Ordinal);
        if (needsBackfill)
        {
            foreach (var item in existing.Where(e => e.ExternalId is not null && remoteIds.Contains(e.ExternalId)))
                AddLinkedTask(item);
            // Run once per calendar, so tasks intentionally deleted later do not reappear on every sync.
            db.Add(new AppSetting { Key = backfillKey, Value = "1" });
        }
        foreach (var local in existing.Where(e => e.ExternalId is not null && !remoteIds.Contains(e.ExternalId)))
        {
            // Never infer a remote deletion for a resource we have not uploaded/acknowledged yet.
            if (local.ETag is null) continue;
            if (local.SyncStatus is SyncStatus.PendingUpload or SyncStatus.Conflict)
            { PreserveConflict(db, local); conflicts++; }
            DetachTask(local); db.Remove(local);
        }
        db.ChangeTracker.DetectChanges();
        var changed = db.ChangeTracker.HasChanges();
        await db.SaveChangesAsync();
        if (changed) calendarUndo.Clear();
        return conflicts;
    });

    public Task<List<CalendarEvent>> ClaimUploadsAsync(string calendarId) => Run(async db =>
    {
        var items = await db.Events.Where(e => (e.CalendarId == null && e.SyncStatus == SyncStatus.Local)
            || (e.CalendarId == calendarId && e.SyncStatus != SyncStatus.Synced && e.SyncStatus != SyncStatus.Conflict)).ToListAsync();
        foreach (var item in items.Where(e => e.ExternalId is null))
        {
            item.CalendarId = calendarId; item.ExternalId = calendarId.TrimEnd('/') + "/" + item.Id.ToString("N") + ".ics";
            item.SyncStatus = SyncStatus.PendingUpload;
        }
        db.ChangeTracker.DetectChanges(); var changed = db.ChangeTracker.HasChanges();
        await db.SaveChangesAsync(); if (changed) calendarUndo.Clear();
        return items;
    });

    public Task AcknowledgeUploadAsync(CalendarEvent sent, CalendarEvent remote) => Run(async db =>
    {
        var local = await db.Events.Include(e => e.Task).SingleOrDefaultAsync(e => e.Id == sent.Id);
        if (local is null) throw new InvalidOperationException("Локальная запись исчезла во время синхронизации.");
        if (local.ExternalId != sent.ExternalId || local.CalendarId != sent.CalendarId) return false;
        // Edits made while HTTP was in flight remain pending against the newly acknowledged ETag.
        var unchanged = local.LastModified == sent.LastModified && local.SyncStatus == sent.SyncStatus && SameContent(local, sent);
        local.ETag = remote.ETag; local.RawICalendar = remote.RawICalendar;
        if (unchanged) { CopyRemote(remote, local); UpdateLinkedTask(local); }
        else if (local.SyncStatus != SyncStatus.PendingDelete) local.SyncStatus = SyncStatus.PendingUpload;
        await db.SaveChangesAsync(); calendarUndo.Clear(); return true;
    });

    public Task AcknowledgeDeleteAsync(CalendarEvent sent) => Run(async db =>
    {
        var local = await db.Events.Include(e => e.Task).SingleOrDefaultAsync(e => e.Id == sent.Id);
        if (local is null) return false;
        if (local.SyncStatus == SyncStatus.PendingDelete) { DetachTask(local); db.Remove(local); }
        else
        {
            // Ctrl+Z during the request restores a new local resource, never an obsolete remote version.
            local.ExternalId = null; local.CalendarId = null; local.ETag = null; local.RawICalendar = null;
            local.SyncStatus = SyncStatus.Local;
        }
        await db.SaveChangesAsync(); calendarUndo.Clear(); return true;
    });

    private static bool SameContent(CalendarEvent a, CalendarEvent b) => a.Title == b.Title && a.Description == b.Description
        && a.Location == b.Location && a.Start == b.Start && a.End == b.End && a.IsAllDay == b.IsAllDay;
    private static void CopyRemote(CalendarEvent source, CalendarEvent destination)
    {
        destination.Title = source.Title; destination.Description = source.Description; destination.Location = source.Location;
        destination.Start = source.Start; destination.End = source.End; destination.IsAllDay = source.IsAllDay;
        destination.ETag = source.ETag; destination.RawICalendar = source.RawICalendar; destination.IsReadOnly = source.IsReadOnly;
        destination.LastModified = DateTime.UtcNow; destination.SyncStatus = SyncStatus.Synced;
    }
    private static void DetachTask(CalendarEvent item)
    {
        if (item.Task is not { } task) return;
        task.CalendarEventId = null; task.ScheduledStart = null; task.ScheduledEnd = null; task.UpdatedAt = DateTime.UtcNow;
        item.Task = null;
    }
    private static void PreserveConflict(PlannerDbContext db, CalendarEvent local)
    {
        var copy = new CalendarEvent { Title = local.Title + " (локальная копия — конфликт)", Description = local.Description,
            Location = local.Location, Start = local.Start, End = local.End, IsAllDay = local.IsAllDay, ColorHex = local.ColorHex };
        var task = local.Task; DetachTask(local);
        if (task is not null) { task.CalendarEventId = copy.Id; copy.Task = task; UpdateLinkedTask(copy); }
        db.Events.Add(copy);
    }
    private static void EnsureEditable(CalendarEvent item)
    {
        if (item.IsReadOnly) throw new InvalidOperationException("Повторы и приглашения редактируются в календаре iCloud.");
        if (item.SyncStatus == SyncStatus.PendingDelete) throw new InvalidOperationException("Событие уже удалено.");
    }
}
