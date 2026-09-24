using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DesktopPlanner.Application;
using DesktopPlanner.Domain;

namespace DesktopPlanner.App;

public sealed record TrackerColor(string Hex, string Name);

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
    private readonly SemaphoreSlim gate = new(1, 1);
    private DateTime month = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private string selectedColorHex = "#5CC8FF";
    private string status = "Выберите цвет и нажмите на день";

    public ObservableCollection<MonthDay> Days { get; } = [];
    public IReadOnlyList<string> WeekdayNames { get; } = ["Пн", "Вт", "Ср", "Чт", "Пт", "Сб", "Вс"];
    public IReadOnlyList<TrackerColor> Colors { get; } =
        [new("#5CC8FF", "Голубой"), new("#7EE2A8", "Зелёный"), new("#FFD166", "Жёлтый"), new("#FF8FA3", "Розовый"), new("#B69CFF", "Фиолетовый"), new("#FF9F5C", "Оранжевый")];
    public DateTime Month { get => month; private set { if (SetProperty(ref month, value)) OnPropertyChanged(nameof(MonthLabel)); } }
    public string MonthLabel => CultureInfo.GetCultureInfo("ru-RU").TextInfo.ToTitleCase(Month.ToString("MMMM yyyy", CultureInfo.GetCultureInfo("ru-RU")));
    public string SelectedColorHex { get => selectedColorHex; private set => SetProperty(ref selectedColorHex, value); }
    public string Status { get => status; private set => SetProperty(ref status, value); }
    public IAsyncRelayCommand PreviousMonthCommand { get; }
    public IAsyncRelayCommand NextMonthCommand { get; }
    public IAsyncRelayCommand TodayCommand { get; }
    public IRelayCommand<string> SelectColorCommand { get; }
    public IAsyncRelayCommand<DateTime> ToggleDayCommand { get; }

    public MonthTrackerViewModel(IPlannerStore store)
    {
        this.store = store;
        PreviousMonthCommand = new AsyncRelayCommand(() => ChangeMonthAsync(-1));
        NextMonthCommand = new AsyncRelayCommand(() => ChangeMonthAsync(1));
        TodayCommand = new AsyncRelayCommand(() => SetMonthAsync(new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1)));
        SelectColorCommand = new RelayCommand<string>(color => { if (!string.IsNullOrWhiteSpace(color)) SelectedColorHex = color; });
        ToggleDayCommand = new AsyncRelayCommand<DateTime>(ToggleDayAsync);
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
        var marks = await store.GetHabitDayMarksAsync(first, first.AddDays(42));
        var byDate = marks.ToDictionary(m => m.Date.Date, m => m.ColorHex);
        Days.Clear();
        for (var i = 0; i < 42; i++)
        {
            var date = first.AddDays(i);
            Days.Add(new MonthDay { Date = date, IsInMonth = date.Month == Month.Month, MarkColor = byDate.GetValueOrDefault(date.Date) });
        }
    }
    private async Task ToggleDayAsync(DateTime date)
    {
        if (date.Month != Month.Month || date.Year != Month.Year) return;
        await gate.WaitAsync();
        try
        {
            var day = Days.First(d => d.Date.Date == date.Date);
            if (string.Equals(day.MarkColor, SelectedColorHex, StringComparison.OrdinalIgnoreCase))
            { await store.DeleteHabitDayMarkAsync(date); day.MarkColor = null; Status = "Отметка очищена"; }
            else
            { await store.SaveHabitDayMarkAsync(new HabitDayMark { Date = date.Date, ColorHex = SelectedColorHex }); day.MarkColor = SelectedColorHex; Status = "Отметка сохранена"; }
        }
        catch (Exception ex) { Serilog.Log.Error(ex, "Month tracker mark failed"); Status = "Ошибка сохранения: " + ex.Message; }
        finally { gate.Release(); }
    }
}
