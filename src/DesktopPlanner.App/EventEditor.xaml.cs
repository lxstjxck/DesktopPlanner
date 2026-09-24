using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using DesktopPlanner.Application;
using DesktopPlanner.Domain;
namespace DesktopPlanner.App;

public partial class EventEditor : UserControl
{
    private readonly CalendarViewModel model;
    private readonly Guid id;
    public event Action? Closed;
    public EventEditor(CalendarViewModel model, CalendarEvent item)
    {
        InitializeComponent(); this.model = model; id = item.Id;
        TitleInput.Text = item.Title; DescriptionInput.Text = item.Description; LocationInput.Text = item.Location;
        StartDate.SelectedDate = item.Start.Date;
        EndDate.SelectedDate = item.IsAllDay ? item.End.AddDays(-1).Date : item.End.Date;
        StartTime.Text = item.Start.ToString("HH:mm"); EndTime.Text = item.End.ToString("HH:mm"); AllDay.IsChecked = item.IsAllDay;
        Loaded += (_, _) => { TitleInput.Focus(); TitleInput.SelectAll(); };
    }
    private void AllDayChanged(object sender, RoutedEventArgs e)
    { if (StartTime is not null && EndTime is not null) StartTime.IsEnabled = EndTime.IsEnabled = AllDay.IsChecked != true; }
    private void Cancel(object sender, RoutedEventArgs e) => Closed?.Invoke();
    private async void Delete(object sender, RoutedEventArgs e)
    {
        IsEnabled = false;
        try { await model.DeleteAsync(id); if (model.LastOperationSucceeded) Closed?.Invoke(); else Error.Text = model.Status; }
        finally { IsEnabled = true; }
    }
    private async void Save(object sender, RoutedEventArgs e)
    {
        if (StartDate.SelectedDate is not { } startDate || EndDate.SelectedDate is not { } endDate)
        { Error.Text = "Укажите даты начала и окончания."; return; }
        var allDay = AllDay.IsChecked == true;
        var startTime = TimeSpan.Zero; var endTime = TimeSpan.Zero;
        if (!allDay && (!TimeSpan.TryParseExact(StartTime.Text.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out startTime) ||
                       !TimeSpan.TryParseExact(EndTime.Text.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out endTime) || startTime.TotalHours >= 24 || endTime.TotalHours >= 24))
        { Error.Text = "Укажите время от 00:00 до 23:59 в формате ЧЧ:мм."; return; }
        if (allDay && endDate == DateTime.MaxValue.Date) { Error.Text = "Выберите более раннюю дату окончания."; return; }
        var edit = new EventEdit(TitleInput.Text, DescriptionInput.Text, LocationInput.Text, startDate + startTime,
            allDay ? endDate.AddDays(1) : endDate + endTime, allDay);
        IsEnabled = false;
        try { await model.EditAsync(id, edit); if (model.LastOperationSucceeded) Closed?.Invoke(); else Error.Text = model.Status; }
        finally { IsEnabled = true; }
    }
}
