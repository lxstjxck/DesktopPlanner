namespace DesktopPlanner.Domain;

public sealed class TaskItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public bool IsCompleted { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int Order { get; set; }
    public DateTime? ScheduledStart { get; set; }
    public DateTime? ScheduledEnd { get; set; }
    public Guid? CalendarEventId { get; set; }
    public void Complete(DateTime now) { IsCompleted = true; CompletedAt = now; UpdatedAt = now; }
    public void Restore(DateTime now) { IsCompleted = false; CompletedAt = null; UpdatedAt = now; }
}
public sealed class UnscheduledEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
public enum SyncStatus { Local, PendingUpload, Synced, Conflict, PendingDelete }
public sealed class CalendarEvent
{
    public string? RawICalendar { get; set; }
    public bool IsReadOnly { get; set; }
    public string? ColorHex { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? ExternalId { get; set; }
    public string? CalendarId { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public bool IsAllDay { get; set; }
    public string Location { get; set; } = "";
    public string? ETag { get; set; }
    public DateTime LastModified { get; set; } = DateTime.UtcNow;
    public SyncStatus SyncStatus { get; set; }
    public TaskItem? Task { get; set; }
    public void SetPeriod(DateTime start, DateTime end)
    {
        if (end <= start) throw new ArgumentException("End must be after start.");
        Start = start; End = end; LastModified = DateTime.UtcNow;
    }
}
public sealed class Note { public int Id { get; set; } = 1; public string Text { get; set; } = ""; }
public sealed class AppSetting { public string Key { get; set; } = ""; public string Value { get; set; } = ""; }
public sealed class CalendarAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string DisplayName { get; set; } = "";
    public string ServerUrl { get; set; } = "";
    public string CredentialKey { get; set; } = "";
}
public sealed class SyncMetadata
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CalendarAccountId { get; set; }
    public string CalendarId { get; set; } = "";
    public string? SyncToken { get; set; }
    public DateTime? LastSyncAt { get; set; }
}
