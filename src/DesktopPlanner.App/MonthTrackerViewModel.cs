using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DesktopPlanner.Application;
using DesktopPlanner.Domain;

namespace DesktopPlanner.App;

public sealed class MonthDay : ObservableObject
{
    private string? markColor;
    public DateTime Date { get; init; }
    public int DayNumber => Date.Day;
    public bool IsInMonth { get; init; }
    public bool IsToday => Date.Date == DateTime.Today;
    public string? MarkColor { get => markColor; set { if (SetProperty(ref markColor, value)) OnPropertyChanged(nameof(MarkBrush)); } }
    public Brush MarkBrush => string.IsNullOrWhiteSpace(MarkColor) ? Brushes.Transparent : new SolidColorBrush((Color)ColorConverter.ConvertFromString(MarkColor));
}

public sealed class MonthTrackerViewModel : ObservableObject
{
    private readonly IPlannerStore store;
    private readonly string trackerId;
    private readonly SemaphoreSlim gate = new(1, 1);
    private DateTime month = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private string selectedColorHex;
    private string status = "Нажмите на день, чтобы поставить или снять отметку";

    public ObservableCollection<MonthDay> Days { get; } = [];
    public IReadOnlyList<string> WeekdayNames { get; } = ["Пн", "Вт", "Ср", "Чт", "Пт", "Сб", "Вс"];
    public DateTime Month { get => month; private set { if (SetProperty(ref month, value)) OnPropertyChanged(nameof(MonthLabel)); } }
    public string MonthLabel => CultureInfo.GetCultureInfo("ru-RU").TextInfo.ToTitleCase(Month.ToString("MMMM yyyy", CultureInfo.GetCultureInfo("ru-RU")));
    public string SelectedColorHex { get => selectedColorHex; private set => SetProperty(ref selectedColorHex, value); }
    public string Status { get => status; private set => SetProperty(ref status, value); }
    public IAsyncRelayCommand PreviousMonthCommand { get; }
    public IAsyncRelayCommand NextMonthCommand { get; }
    public IAsyncRelayCommand TodayCommand { get; }
    public IAsyncRelayCommand<DateTime> ToggleDayCommand { get; }

    public MonthTrackerViewModel(IPlannerStore store) : this(store, WidgetLayout.DefaultTrackerId, "#5CC8FF") { }

    public MonthTrackerViewModel(IPlannerStore store, string trackerId, string colorHex)
    {
        this.store = store; this.trackerId = trackerId; selectedColorHex = colorHex;
        PreviousMonthCommand = new AsyncRelayCommand(() => ChangeMonthAsync(-1));
        NextMonthCommand = new AsyncRelayCommand(() => ChangeMonthAsync(1));
        TodayCommand = new AsyncRelayCommand(() => SetMonthAsync(new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1)));
        ToggleDayCommand = new AsyncRelayCommand<DateTime>(ToggleDayAsync);
    }

    public void SetColor(string colorHex)
    {
        if (string.IsNullOrWhiteSpace(colorHex) || !SetProperty(ref selectedColorHex, colorHex)) return;
        foreach (var day in Days.Where(day => day.MarkColor is not null)) day.MarkColor = colorHex;
    }

    public Task LoadAsync() => LoadMonthAsync(Month);
    public void ClearAfterReset()
    {
        foreach (var day in Days) day.MarkColor = null;
        Status = "Отметки удалены";
    }
    private async Task ChangeMonthAsync(int offset) => await SetMonthAsync(Month.AddMonths(offset));
    private async Task SetMonthAsync(DateTime value)
    {
        await gate.WaitAsync();
        try { await LoadMonthCoreAsync(new DateTime(value.Year, value.Month, 1)); }
        catch (Exception ex) { Serilog.Log.Error(ex, "Month tracker operation failed"); Status = "Ошибка сохранения: " + ex.Message; }
        finally { gate.Release(); }
    }
    private async Task LoadMonthAsync(DateTime value)
    {
        await gate.WaitAsync();
        try { await LoadMonthCoreAsync(value); }
        finally { gate.Release(); }
    }
    private async Task LoadMonthCoreAsync(DateTime value)
    {
        Month = new DateTime(value.Year, value.Month, 1);
        var first = Month.AddDays(-(int)(Month.DayOfWeek == DayOfWeek.Sunday ? 6 : Month.DayOfWeek - DayOfWeek.Monday));
        var marks = await store.GetHabitDayMarksAsync(trackerId, first, first.AddDays(42));
        var markedDates = marks.Select(mark => mark.Date.Date).ToHashSet();
        Days.Clear();
        for (var i = 0; i < 42; i++)
        {
            var date = first.AddDays(i);
            Days.Add(new MonthDay { Date = date, IsInMonth = date.Month == Month.Month, MarkColor = markedDates.Contains(date.Date) ? SelectedColorHex : null });
        }
    }
    private async Task ToggleDayAsync(DateTime date)
    {
        if (date.Month != Month.Month || date.Year != Month.Year) return;
        await gate.WaitAsync();
        try
        {
            var day = Days.First(day => day.Date.Date == date.Date);
            if (day.MarkColor is not null)
            { await store.DeleteHabitDayMarkAsync(trackerId, date); day.MarkColor = null; Status = "Отметка очищена"; }
            else
            { await store.SaveHabitDayMarkAsync(new HabitDayMark { TrackerId = trackerId, Date = date.Date, ColorHex = SelectedColorHex }); day.MarkColor = SelectedColorHex; Status = "Отметка сохранена"; }
        }
        catch (Exception ex) { Serilog.Log.Error(ex, "Month tracker mark failed"); Status = "Ошибка сохранения: " + ex.Message; }
        finally { gate.Release(); }
    }
}
