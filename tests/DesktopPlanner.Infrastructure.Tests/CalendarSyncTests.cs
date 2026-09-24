using System.Net;
using System.Text;
using DesktopPlanner.Application;
using DesktopPlanner.Domain;

namespace DesktopPlanner.Infrastructure.Tests;

public sealed class CalendarSyncTests : IDisposable
{
    private const string CalendarId = "https://p01-caldav.icloud.com/123/calendars/work/";
    private readonly string directory = Path.Combine(Path.GetTempPath(), "DesktopPlannerSync", Guid.NewGuid().ToString("N"));
    private SqlitePlannerStore Create() => new(Path.Combine(directory, "planner.db"));
    private static EventEdit Edit(CalendarEvent e, string title) => new(title, e.Description, e.Location, e.Start, e.End, e.IsAllDay);

    [Fact]
    public void ICalendarRoundtripPreservesDatesUnicodeAlarmsAndExpandsExceptions()
    {
        var item = new CalendarEvent { Title = "Встреча, проект; А", Description = "Первая строка\nВторая", Start = new DateTime(2026, 9, 21, 10, 0, 0), End = new DateTime(2026, 9, 21, 11, 0, 0) };
        var restored = CalendarCodec.Read(CalendarCodec.Write(item));
        Assert.Equal(item.Start, restored.Start); Assert.Equal(item.Title, restored.Title); Assert.Equal(item.Description, restored.Description);
        item.IsAllDay = true; item.Start = item.Start.Date; item.End = item.Start.AddDays(2);
        restored = CalendarCodec.Read(CalendarCodec.Write(item));
        Assert.True(restored.IsAllDay); Assert.Equal(item.End, restored.End);
        const string series = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Test//EN\r\nBEGIN:VEVENT\r\nUID:series\r\nDTSTAMP:20260101T000000Z\r\nDTSTART:20260921T100000\r\nDTEND:20260921T110000\r\nRRULE:FREQ=DAILY;COUNT=4\r\nEXDATE:20260922T100000\r\nSUMMARY:Repeat\r\nBEGIN:VALARM\r\nACTION:DISPLAY\r\nTRIGGER:-PT15M\r\nDESCRIPTION:Reminder\r\nEND:VALARM\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var recurring = CalendarCodec.Read(series);
        Assert.True(recurring.IsReadOnly);
        var occurrences = CalendarCodec.Visible(recurring, new DateTime(2026, 9, 21), new DateTime(2026, 9, 28)).ToArray();
        Assert.Equal(new[] { 21, 23, 24 }, occurrences.Select(e => e.Start.Day));
        var simple = CalendarCodec.Read(series.Replace("RRULE:FREQ=DAILY;COUNT=4\r\n", "").Replace("EXDATE:20260922T100000\r\n", ""));
        simple.Title = "Changed";
        Assert.Contains("BEGIN:VALARM", CalendarCodec.Write(simple));
    }

    [Fact]
    public async Task InterruptedCreateDoesNotDuplicateAndEditsDuringUploadRemainPending()
    {
        using var store = Create(); await store.InitializeAsync();
        var task = new TaskItem { Title = "Original" }; await store.SaveTaskAsync(task);
        await store.ScheduleAsync(CalendarSource.Task, task.Id, DateTime.Today.AddHours(10));
        using var sync = new CalendarSyncService(store, new NoCredentials()); var provider = new MemoryProvider { FailAfterSave = true };
        await Assert.ThrowsAsync<HttpRequestException>(() => sync.SynchronizeAsync(provider, CalendarId, default));
        Assert.Single(provider.Items);
        provider.FailAfterSave = false; await sync.SynchronizeAsync(provider, CalendarId, default);
        var local = Assert.Single(await store.GetSyncEventsAsync(CalendarId));
        Assert.Equal(SyncStatus.Synced, local.SyncStatus); Assert.Single(provider.Items);
        await store.UpdateEventAsync(local.Id, Edit(local, "First edit"));
        provider.DuringSave = sent => store.UpdateEventAsync(sent.Id, Edit(sent, "While uploading"));
        await sync.SynchronizeAsync(provider, CalendarId, default);
        local = Assert.Single(await store.GetSyncEventsAsync(CalendarId));
        Assert.Equal(SyncStatus.PendingUpload, local.SyncStatus); Assert.Equal("While uploading", local.Title);
        provider.DuringSave = null; await sync.SynchronizeAsync(provider, CalendarId, default);
        Assert.Equal("While uploading", Assert.Single(provider.Items.Values).Title);
        await store.DeleteEventAsync(local.Id); await sync.SynchronizeAsync(provider, CalendarId, default);
        Assert.Empty(provider.Items); Assert.Empty(await store.GetSyncEventsAsync(CalendarId));
        Assert.Null(Assert.Single(await store.GetTasksAsync()).CalendarEventId);
    }

    [Fact]
    public async Task ConcurrentChangesPreserveBothVersionsAndTaskLink()
    {
        using var store = Create(); await store.InitializeAsync();
        var task = new TaskItem { Title = "Plan" }; await store.SaveTaskAsync(task);
        await store.ScheduleAsync(CalendarSource.Task, task.Id, DateTime.Today.AddHours(9));
        using var sync = new CalendarSyncService(store, new NoCredentials()); var provider = new MemoryProvider();
        await sync.SynchronizeAsync(provider, CalendarId, default);
        var local = Assert.Single(await store.GetSyncEventsAsync(CalendarId));
        await store.UpdateEventAsync(local.Id, Edit(local, "Local edit"));
        await store.SetEventColorAsync(local.Id, "#123456");
        provider.Items[local.ExternalId!].Title = "Remote edit"; provider.Items[local.ExternalId!].ETag = "\"remote-new\"";
        await sync.SynchronizeAsync(provider, CalendarId, default);
        var events = await store.GetSyncEventsAsync(CalendarId);
        Assert.Equal(2, events.Count); Assert.Equal(2, provider.Items.Count);
        var copy = Assert.Single(events, e => e.Title.Contains("локальная копия"));
        Assert.Equal("#123456", copy.ColorHex); Assert.Equal(copy.Id, Assert.Single(await store.GetTasksAsync(), t => t.Id == task.Id).CalendarEventId);
        Assert.Contains(events, e => e.Id == local.Id && e.Title == "Remote edit");
        Assert.False(await store.UndoCalendarAsync());
        // Remote deletion never silently erases an unsent local edit.
        await store.UpdateEventAsync(copy.Id, Edit(copy, "Unsent")); provider.Items.Remove(copy.ExternalId!);
        await sync.SynchronizeAsync(provider, CalendarId, default);
        Assert.Contains(await store.GetSyncEventsAsync(CalendarId), e => e.Title.StartsWith("Unsent"));
    }

    [Fact]
    public async Task CalDavUsesConditionalWritesRejectsUnsafeRedirectAndIncompleteInventory()
    {
        var id = CalendarId + "one.ics";
        using var provider = new ICloudCalendarProvider("account", "app-password", new Handler(request =>
        {
            Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
            if (request.Method == HttpMethod.Put) { Assert.Contains("*", request.Headers.GetValues("If-None-Match")); return new(HttpStatusCode.PreconditionFailed); }
            if (request.Method == HttpMethod.Delete) { Assert.Equal("\"v1\"", Assert.Single(request.Headers.GetValues("If-Match"))); return new(HttpStatusCode.PreconditionFailed); }
            return new(HttpStatusCode.MultiStatus) { Content = new StringContent("<d:multistatus xmlns:d='DAV:'><d:response><d:href>" + id + "</d:href><d:status>HTTP/1.1 403 Forbidden</d:status></d:response></d:multistatus>") };
        }));
        var item = new CalendarEvent { CalendarId = CalendarId, ExternalId = id, Start = DateTime.Today, End = DateTime.Today.AddHours(1) };
        await Assert.ThrowsAsync<CalendarConflictException>(() => provider.SaveAsync(item, null, default));
        await Assert.ThrowsAsync<CalendarConflictException>(() => provider.DeleteAsync(CalendarId, id, "\"v1\"", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetChangesAsync(CalendarId, new Dictionary<string, string?>(), default));
        var calls = 0;
        using var redirect = new ICloudCalendarProvider("account", "secret", new Handler(_ =>
        { calls++; var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new Uri("https://example.com/steal"); return response; }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => redirect.DiscoverAsync(new CalendarAccount(), default));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ImportedEventsCreateTasksBackfillOnceAndPreserveCompletion()
    {
        using var store = Create(); await store.InitializeAsync();
        var imported = new CalendarEvent { CalendarId = CalendarId, ExternalId = CalendarId + "imported.ics",
            Title = "From iCloud", Start = DateTime.Today.AddHours(10), End = DateTime.Today.AddHours(11),
            ETag = "\"1\"", SyncStatus = SyncStatus.Synced };
        // Existing installs already have downloaded events with no linked tasks and unchanged ETags.
        await using (var db = store.CreateContext()) { db.Events.Add(imported); await db.SaveChangesAsync(); }
        var unchanged = new CalendarChangeSet([], [imported.ExternalId]);
        await store.ApplyRemoteAsync(CalendarId, unchanged);
        var task = Assert.Single(await store.GetTasksAsync());
        Assert.Equal(imported.Id, task.CalendarEventId); Assert.Equal(imported.Start, task.ScheduledStart);
        task.Complete(DateTime.UtcNow); await store.SaveTaskAsync(task);
        imported.Title = "Renamed in iCloud"; imported.Start = imported.Start.AddHours(1); imported.End = imported.End.AddHours(1); imported.ETag = "\"2\"";
        await store.ApplyRemoteAsync(CalendarId, new CalendarChangeSet([imported], [imported.ExternalId]));
        task = Assert.Single(await store.GetTasksAsync());
        Assert.True(task.IsCompleted); Assert.Equal("Renamed in iCloud", task.Title); Assert.Equal(imported.Start, task.ScheduledStart);
        await store.ApplyRemoteAsync(CalendarId, unchanged);
        Assert.Single(await store.GetTasksAsync());
        await store.DeleteTaskAsync(task.Id); await store.ApplyRemoteAsync(CalendarId, unchanged);
        Assert.Empty(await store.GetTasksAsync());
        var second = new CalendarEvent { CalendarId = CalendarId, ExternalId = CalendarId + "second.ics", Title = "New event",
            Start = imported.Start, End = imported.End, ETag = "\"1\"", SyncStatus = SyncStatus.Synced };
        await store.ApplyRemoteAsync(CalendarId, new CalendarChangeSet([second], [imported.ExternalId, second.ExternalId]));
        Assert.Equal(second.Id, Assert.Single(await store.GetTasksAsync()).CalendarEventId);
    }

    [Fact]
    public async Task CalDavIgnoresCalendarCollectionInEventInventory()
    {
        using var provider = new ICloudCalendarProvider("account", "password", new Handler(_ =>
            new(HttpStatusCode.MultiStatus) { Content = new StringContent(
                "<d:multistatus xmlns:d='DAV:'><d:response><d:href>/123/calendars/work/</d:href>"
                + "<d:propstat><d:prop><d:getetag>\"collection\"</d:getetag></d:prop>"
                + "<d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response></d:multistatus>") }));
        var changes = await provider.GetChangesAsync(CalendarId, new Dictionary<string, string?>(), default);
        Assert.Empty(changes.Events); Assert.Empty(changes.RemoteIds);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { var result = reply(request); result.RequestMessage = request; return Task.FromResult(result); }
    }
    private sealed class MemoryProvider : ICalendarProvider
    {
        public Dictionary<string, CalendarEvent> Items { get; } = [];
        public bool FailAfterSave { get; set; }
        public Func<CalendarEvent, Task>? DuringSave { get; set; }
        private int version;
        private static CalendarEvent Copy(CalendarEvent e) => new() { Id = e.Id, Title = e.Title, Description = e.Description, Location = e.Location,
            Start = e.Start, End = e.End, IsAllDay = e.IsAllDay, CalendarId = e.CalendarId, ExternalId = e.ExternalId,
            ETag = e.ETag, RawICalendar = e.RawICalendar, SyncStatus = SyncStatus.Synced };
        public Task<IReadOnlyList<RemoteCalendar>> DiscoverAsync(CalendarAccount account, CancellationToken ct) => Task.FromResult<IReadOnlyList<RemoteCalendar>>([new(CalendarId, "Test")]);
        public Task<CalendarChangeSet> GetChangesAsync(string calendar, IReadOnlyDictionary<string, string?> known, CancellationToken ct)
            => Task.FromResult(new CalendarChangeSet(Items.Where(p => !known.TryGetValue(p.Key, out var etag) || etag != p.Value.ETag).Select(p => Copy(p.Value)).ToArray(), Items.Keys.ToArray()));
        public async Task<CalendarEvent> SaveAsync(CalendarEvent item, string? expectedETag, CancellationToken ct)
        {
            if (Items.TryGetValue(item.ExternalId!, out var previous) && previous.ETag != expectedETag) throw new CalendarConflictException();
            var saved = Copy(item); saved.ETag = "\"" + ++version + "\""; saved.RawICalendar = CalendarCodec.Write(saved);
            Items[item.ExternalId!] = saved;
            if (FailAfterSave) throw new HttpRequestException("Connection lost after PUT");
            if (DuringSave is not null) await DuringSave(item);
            return Copy(saved);
        }
        public Task DeleteAsync(string calendar, string id, string? expectedETag, CancellationToken ct)
        { if (Items.TryGetValue(id, out var old) && old.ETag != expectedETag) throw new CalendarConflictException(); Items.Remove(id); return Task.CompletedTask; }
    }
    private sealed class NoCredentials : ICredentialStore
    {
        public Task<string?> GetAsync(string key, CancellationToken ct) => throw new NotSupportedException();
        public Task SetAsync(string key, string secret, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(string key, CancellationToken ct) => throw new NotSupportedException();
    }
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
