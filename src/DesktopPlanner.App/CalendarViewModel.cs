using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DesktopPlanner.Application;
using DesktopPlanner.Domain;
namespace DesktopPlanner.App;

public sealed class CalendarViewModel : ObservableObject
{
    private readonly CalendarService service;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly HashSet<Task> pending = [];
    private DateTime weekStart = WeekGeometry.Monday(DateTime.Today);
    private IReadOnlyList<CalendarEvent> events = [];
    private IReadOnlyList<UnscheduledEvent> inbox = [];
    private string title = "", description = "", status = "Перетащите задачу или событие на сетку";
    public DateTime WeekStart { get => weekStart; private set { SetProperty(ref weekStart, value); OnPropertyChanged(nameof(WeekLabel)); } }
    public string WeekLabel => $"{WeekStart:dd.MM} — {WeekStart.AddDays(6):dd.MM.yyyy}";
    public IReadOnlyList<CalendarEvent> Events { get => events; private set => SetProperty(ref events, value); }
    public IReadOnlyList<UnscheduledEvent> Inbox { get => inbox; private set => SetProperty(ref inbox, value); }
    public string NewTitle { get => title; set => SetProperty(ref title, value); }
    public string NewDescription { get => description; set => SetProperty(ref description, value); }
    public string Status { get => status; private set => SetProperty(ref status, value); }
    public bool LastOperationSucceeded { get; private set; } = true;
    public event Func<Task>? CalendarChanged;
    public event Action? ShowRequested;
    public CalendarEvent? NavigationTarget { get; private set; }
    public Task ShowTaskAsync(TaskItem task) => Queue(async () =>
    {
        if (task.CalendarEventId is not Guid id || task.ScheduledStart is not DateTime start) return;
        await LoadWeekAsync(WeekGeometry.Monday(start));
        var item = Events.FirstOrDefault(e => e.Id == id)
            ?? throw new InvalidOperationException("Событие уже удалено или перенесено. Обновите календарь.");
        SetViewport(Math.Max(0, item.Start.TimeOfDay.TotalHours - .5), true);
        NavigationTarget = item;
        OnPropertyChanged(nameof(NavigationTarget));
        ShowRequested?.Invoke();
        LastOperationSucceeded = true;
        Status = $"{item.Title} · {item.Start:dd.MM HH:mm}";
    }, false);
    public IAsyncRelayCommand AddInboxCommand { get; }
    public IAsyncRelayCommand<UnscheduledEvent> DeleteInboxCommand { get; }
    public IAsyncRelayCommand PreviousWeekCommand { get; }
    public IAsyncRelayCommand NextWeekCommand { get; }
    public IAsyncRelayCommand TodayCommand { get; }
    public IAsyncRelayCommand UndoCommand { get; }
    public double ScrollHour { get; private set; } = 8;
    public bool DetailedGrid { get; private set; } = true;
    private bool viewportDirty;
    private sealed record Viewport(double Hour, bool Detailed);
    public CalendarViewModel(CalendarService service)
    {
        this.service = service;
        AddInboxCommand = new AsyncRelayCommand(() => Queue(async () =>
        {
            var value = NewTitle; var details = NewDescription;
            await service.AddInboxAsync(value, details);
            if (NewTitle == value) NewTitle = ""; if (NewDescription == details) NewDescription = "";
            await RefreshCoreAsync();
        }));
        DeleteInboxCommand = new AsyncRelayCommand<UnscheduledEvent>(item => item is null ? Task.CompletedTask : Queue(async () =>
        { await service.DeleteInboxAsync(item.Id); await RefreshCoreAsync(); }));
        PreviousWeekCommand = new AsyncRelayCommand(() => NavigateAsync(-7));
        NextWeekCommand = new AsyncRelayCommand(() => NavigateAsync(7));
        TodayCommand = new AsyncRelayCommand(() => Queue(() => LoadWeekAsync(WeekGeometry.Monday(DateTime.Today))));
        UndoCommand = new AsyncRelayCommand(() => Queue(async () =>
        { var undone = await service.UndoAsync(); await AfterMutationAsync(); Status = undone ? "Действие отменено" : "Нет действий для отмены"; }, false));
    }
    public Task LoadAsync() => Queue(async () =>
    {
        var json = await service.GetViewportAsync();
        try
        {
            if (json is not null && System.Text.Json.JsonSerializer.Deserialize<Viewport>(json) is { } value && double.IsFinite(value.Hour))
            { ScrollHour = Math.Clamp(value.Hour, 0, 24); DetailedGrid = value.Detailed; }
        }
        catch (System.Text.Json.JsonException) { /* Ignore a corrupt optional view preference. */ }
        await RefreshCoreAsync();
    });
    public void SetViewport(double hour, bool detailed)
    { ScrollHour = Math.Clamp(hour, 0, 24); DetailedGrid = detailed; viewportDirty = true; }
    public Task SaveViewportAsync() => Queue(async () =>
    {
        if (!viewportDirty) return;
        var value = new Viewport(ScrollHour, DetailedGrid);
        await service.SaveViewportAsync(System.Text.Json.JsonSerializer.Serialize(value));
        if (value == new Viewport(ScrollHour, DetailedGrid)) viewportDirty = false;
    }, false);
    public Task EditAsync(Guid id, EventEdit edit) => Queue(async () =>
    { await service.UpdateAsync(id, edit); await AfterMutationAsync(); });
    public Task SetColorAsync(Guid id, string? colorHex) => Queue(async () =>
    { await service.SetColorAsync(id, colorHex); await RefreshCoreAsync(); });
    private Task NavigateAsync(int days) => Queue(() => LoadWeekAsync(WeekStart.AddDays(days)));
    private async Task LoadWeekAsync(DateTime start)
    {
        var loaded = await service.GetEventsAsync(start, start.AddDays(7));
        WeekStart = start; Events = loaded;
    }
    private async Task RefreshCoreAsync()
    {
        var loadedEvents = await service.GetEventsAsync(WeekStart, WeekStart.AddDays(7));
        var loadedInbox = await service.GetInboxAsync();
        Events = loadedEvents; Inbox = loadedInbox;
    }
    public Task ScheduleAsync(CalendarDrag drag, DateTime start) => Queue(async () =>
    { await service.ScheduleAsync(drag, start); await AfterMutationAsync(); });
    public Task ResizeAsync(Guid id, DateTime end) => Queue(async () =>
    { await service.ResizeAsync(id, end); await AfterMutationAsync(); });
    public Task DeleteAsync(Guid id) => Queue(async () =>
    { await service.DeleteEventAsync(id); await AfterMutationAsync(); });
    private async Task AfterMutationAsync()
    {
        await RefreshCoreAsync();
        if (CalendarChanged is { } changed) foreach (Func<Task> handler in changed.GetInvocationList()) await handler();
    }
    public Task RefreshAfterSyncAsync() => Queue(AfterMutationAsync, false);
    private Task Queue(Func<Task> action, bool announce = true)
    {
        var task = RunAsync(action, announce); pending.Add(task);
        _ = RemoveWhenFinishedAsync(task); return task;
    }
    private async Task RemoveWhenFinishedAsync(Task task) { await task; pending.Remove(task); }
    private async Task RunAsync(Func<Task> action, bool announce)
    {
        await operationGate.WaitAsync();
        try { await action(); if (announce) { LastOperationSucceeded = true; Status = "Сохранено · время местное"; } }
        catch (Exception ex) { LastOperationSucceeded = false; Serilog.Log.Error(ex, "Calendar operation failed"); Status = "Ошибка: " + ex.Message; }
        finally { operationGate.Release(); }
    }
    public async Task WhenIdleAsync() { while (pending.Count > 0) await Task.WhenAll(pending.ToArray()); }
}
