using DesktopPlanner.Application;
using DesktopPlanner.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace DesktopPlanner.Infrastructure;

public sealed partial class SqlitePlannerStore
{
    private sealed record UndoRow(Type Type, object Key, PropertyValues? Before, PropertyValues? After);
    private readonly List<List<UndoRow>> calendarUndo = [];
    public Task SetEventColorAsync(Guid id, string? colorHex) => Run(async db =>
    {
        if (colorHex is not null && (colorHex.Length != 7 || colorHex[0] != '#' || !colorHex.Skip(1).All(Uri.IsHexDigit)))
            throw new ArgumentException("Цвет должен быть в формате #RRGGBB.");
        var item = await db.Events.FindAsync(id) ?? throw new InvalidOperationException("Событие уже удалено.");
        if (item.SyncStatus == SyncStatus.PendingDelete) throw new InvalidOperationException("Событие уже удалено.");
        item.ColorHex = colorHex?.ToUpperInvariant();
        await SaveCalendarAsync(db); return true;
    });
    private static readonly string[] TaskScheduleFields = [nameof(TaskItem.CalendarEventId), nameof(TaskItem.ScheduledStart), nameof(TaskItem.ScheduledEnd)];
    private async Task SaveCalendarAsync(PlannerDbContext db)
    {
        db.ChangeTracker.DetectChanges();
        var changes = db.ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(e => new UndoRow(e.Metadata.ClrType, e.Property("Id").CurrentValue!,
                e.State == EntityState.Added ? null : e.OriginalValues.Clone(),
                e.State == EntityState.Deleted ? null : e.CurrentValues.Clone())).ToList();
        // EF saves the event, inbox and linked task together in one transaction.
        await db.SaveChangesAsync();
        if (changes.Count == 0) return;
        calendarUndo.Add(changes);
        if (calendarUndo.Count > 50) calendarUndo.RemoveAt(0);
    }
    public Task UpdateEventAsync(Guid id, EventEdit edit) => Run(async db =>
    {
        if (string.IsNullOrWhiteSpace(edit.Title)) throw new ArgumentException("Введите название события.");
        if (edit.End <= edit.Start) throw new ArgumentException("Окончание должно быть позже начала.");
        if (edit.IsAllDay && (edit.Start.TimeOfDay != TimeSpan.Zero || edit.End.TimeOfDay != TimeSpan.Zero))
            throw new ArgumentException("Для события на весь день укажите даты без времени.");
        var item = await db.Events.Include(e => e.Task).SingleOrDefaultAsync(e => e.Id == id)
            ?? throw new InvalidOperationException("Событие уже удалено.");
        if (item.SyncStatus == SyncStatus.PendingDelete) throw new InvalidOperationException("Событие уже удалено.");
        item.Title = edit.Title.Trim(); item.Description = edit.Description.Trim(); item.Location = edit.Location.Trim();
        EnsureEditable(item);
        item.IsAllDay = edit.IsAllDay; item.SetPeriod(edit.Start, edit.End);
        UpdateLinkedTask(item); MarkChanged(item);
        await SaveCalendarAsync(db); return true;
    });
    public Task<bool> UndoCalendarAsync() => Run(async db =>
    {
        if (calendarUndo.Count == 0) return false;
        var changes = calendarUndo[^1];
        foreach (var change in changes)
        {
            var current = await db.FindAsync(change.Type, change.Key);
            // A task can be completed, renamed or deleted independently of the calendar.
            if (change.Type == typeof(TaskItem) && current is null) continue;
            if (change.After is null ? current is not null : current is null)
                throw new InvalidOperationException("Запись изменилась после действия; отмена не выполнена.");
            if (current is not null && change.After is not null)
            {
                var values = db.Entry(current).CurrentValues;
                var fields = change.Type == typeof(TaskItem) ? TaskScheduleFields : change.After.Properties.Select(p => p.Name);
                if (fields.Any(name => !Equals(values[name], change.After[name])))
                    throw new InvalidOperationException("Запись изменилась после действия; отмена не выполнена.");
            }
        }
        foreach (var change in changes)
        {
            var current = await db.FindAsync(change.Type, change.Key);
            if (change.Type == typeof(TaskItem))
            {
                if (current is not null && change.Before is not null)
                    foreach (var name in TaskScheduleFields) db.Entry(current).Property(name).CurrentValue = change.Before[name];
            }
            else if (change.Before is null) { if (current is not null) db.Remove(current); }
            else if (current is null) db.Add(change.Before.ToObject());
            else db.Entry(current).CurrentValues.SetValues(change.Before);
        }
        await db.SaveChangesAsync(); calendarUndo.RemoveAt(calendarUndo.Count - 1); return true;
    });
    public Task<string?> GetSettingAsync(string key) => Run(async db => (await db.Set<AppSetting>().FindAsync(key))?.Value);
    public Task SaveSettingAsync(string key, string value) => Run(async db =>
    {
        var item = await db.Set<AppSetting>().FindAsync(key);
        if (item is null) db.Add(new AppSetting { Key = key, Value = value }); else item.Value = value;
        await db.SaveChangesAsync(); return true;
    });
}
