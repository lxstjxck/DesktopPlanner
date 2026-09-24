using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DesktopPlanner.Application;
using DesktopPlanner.Domain;
namespace DesktopPlanner.App;

public sealed class PlannerViewModel : ObservableObject
{
    private readonly PlannerService service;
    public CalendarViewModel Calendar { get; }
    private string newTitle = "", noteText = "", status = "Все изменения сохраняются автоматически";
    private bool loaded;
    public ObservableCollection<TaskItem> Todo { get; } = [];
    public ObservableCollection<TaskItem> Completed { get; } = [];
    public string NewTitle { get => newTitle; set => SetProperty(ref newTitle, value); }
    public string NoteText { get => noteText; set { if (SetProperty(ref noteText, value) && loaded) NoteChanged?.Invoke(); } }
    public string Status { get => status; private set => SetProperty(ref status, value); }
    public event Action? NoteChanged;
    public IAsyncRelayCommand AddCommand { get; }
    public IAsyncRelayCommand<TaskItem> CompleteCommand { get; }
    public IAsyncRelayCommand<TaskItem> RestoreCommand { get; }
    public IAsyncRelayCommand<TaskItem> DeleteCommand { get; }
    public IAsyncRelayCommand<TaskItem> ShowInCalendarCommand { get; }
    public PlannerViewModel(PlannerService service, CalendarViewModel calendar)
    {
        this.service = service; Calendar = calendar; Calendar.CalendarChanged += RefreshAsync;
        AddCommand = new AsyncRelayCommand(() => Guard(async () => { var title = NewTitle; await service.AddAsync(title); if (NewTitle == title) NewTitle = ""; await RefreshAsync(); }));
        CompleteCommand = new AsyncRelayCommand<TaskItem>(t => Mutate(t, service.CompleteAsync));
        RestoreCommand = new AsyncRelayCommand<TaskItem>(t => Mutate(t, service.RestoreAsync));
        DeleteCommand = new AsyncRelayCommand<TaskItem>(t => Mutate(t, service.DeleteAsync));
        ShowInCalendarCommand = new AsyncRelayCommand<TaskItem>(task => Guard(async () =>
        {
            if (task is null) return;
            var current = (await service.GetTasksAsync()).FirstOrDefault(t => t.Id == task.Id);
            if (current?.CalendarEventId is null || current.ScheduledStart is null)
            { await RefreshAsync(); Status = "Задача больше не запланирована в календаре"; return; }
            await Calendar.ShowTaskAsync(current);
        }), task => task?.CalendarEventId is not null && task.ScheduledStart is not null);
    }
    public async Task LoadAsync() { NoteText = await service.GetNoteAsync(); await RefreshAsync(); await Calendar.LoadAsync(); loaded = true; }
    private async Task Mutate(TaskItem? task, Func<TaskItem, Task> operation)
    { if (task is not null) await Guard(async () => { await operation(task); await RefreshAsync(); }); }
    private async Task Guard(Func<Task> action)
    {
        try { await action(); Status = "Сохранено"; }
        catch (Exception ex) { Serilog.Log.Error(ex, "Planner operation failed"); Status = "Ошибка сохранения: " + ex.Message; }
    }
    public async Task SaveNoteAsync() { await service.SaveNoteAsync(NoteText); Status = "Заметка сохранена"; }
    public Task? ReorderTask { get; private set; }
    public Task ReorderAsync(Guid source, Guid target) => ReorderTask = ReorderCoreAsync(source, target);
    private async Task ReorderCoreAsync(Guid source, Guid target)
    {
        var ids = Todo.Select(t => t.Id).ToList();
        var index = ids.IndexOf(target);
        if (index < 0 || !ids.Remove(source)) return;
        ids.Insert(index, source);
        await Guard(async () => { await service.ReorderAsync(ids); await RefreshAsync(); });
    }
    private async Task RefreshAsync()
    {
        var tasks = await service.GetTasksAsync();
        Todo.Clear(); Completed.Clear();
        foreach (var task in tasks.Where(t => !t.IsCompleted)) Todo.Add(task);
        foreach (var task in tasks.Where(t => t.IsCompleted).OrderByDescending(t => t.CompletedAt)) Completed.Add(task);
    }
}


