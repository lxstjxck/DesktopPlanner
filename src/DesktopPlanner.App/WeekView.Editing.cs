using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using DesktopPlanner.Domain;
namespace DesktopPlanner.App;

public partial class WeekView
{
    private Guid? selectedEvent;
    private bool restoringViewport = true;
    private readonly DispatcherTimer clockTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer viewportTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly List<UIElement> currentTimeVisuals = [];
    private async void RestoreViewport(object sender, RoutedEventArgs e)
    {
        restoringViewport = true; AttachModel();
        if (model is null) return;
        detailed = model.DetailedGrid; DensityButton.Content = detailed ? "Сутки" : "По часам";
        var hour = model.ScrollHour; Render();
        await Dispatcher.InvokeAsync(() => Scroller.ScrollToVerticalOffset(hour * hourHeight), DispatcherPriority.ContextIdle);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        if (!IsLoaded) return;
        restoringViewport = false; clockTimer.Start(); DrawCurrentTime();
        QueueTaskNavigation();
    }
    private CalendarEvent? appliedNavigation;
    private void QueueTaskNavigation()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!IsLoaded || restoringViewport || model?.NavigationTarget is not { } item || ReferenceEquals(appliedNavigation, item)) return;
            appliedNavigation = item;
            if (item.Start < model.WeekStart || item.Start >= model.WeekStart.AddDays(7)) return;
            CloseEditor(); selectedEvent = item.Id;
            detailed = true; DensityButton.Content = "Сутки";
            BeginAnimation(SmoothOffsetProperty, null); Render(); Scroller.UpdateLayout();
            var day = (item.Start.Date - model.WeekStart).Days;
            Scroller.ScrollToHorizontalOffset(Math.Max(0, DesktopPlanner.Application.WeekGeometry.TimeGutter + day * dayWidth - (Scroller.ViewportWidth - dayWidth) / 2));
            AnimateScroll(Math.Max(0, item.Start.TimeOfDay.TotalHours - .5) * hourHeight);
            model.SetViewport(Math.Max(0, item.Start.TimeOfDay.TotalHours - .5), true);
            viewportTimer.Stop(); viewportTimer.Start();
        }, DispatcherPriority.ContextIdle);
    }
    private void RememberViewport()
    {
        model?.SetViewport(Scroller.VerticalOffset / hourHeight, detailed);
        viewportTimer.Stop(); viewportTimer.Start();
    }
    private void OpenEditor(CalendarEvent item)
    {
        if (model is null || item.IsReadOnly) return;
        var editor = new EventEditor(model, item);
        editor.Closed += CloseEditor; EditorHost.Content = editor; EditorOverlay.Visibility = Visibility.Visible;
    }
    private void CloseEditor()
    { EditorOverlay.Visibility = Visibility.Collapsed; EditorHost.Content = null; Board.Focus(); }
    private async void CalendarKeys(object sender, KeyEventArgs e)
    {
        if (EditorOverlay.Visibility == Visibility.Visible)
        { if (e.Key == Key.Escape && EditorHost.Content is UIElement { IsEnabled: true }) { CloseEditor(); e.Handled = true; } return; }
        if (model is null || Keyboard.FocusedElement is TextBoxBase) return;
        if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control)
        { e.Handled = true; await model.UndoCommand.ExecuteAsync(null); }
        else if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None && selectedEvent is Guid id)
        { e.Handled = true; await model.DeleteAsync(id); selectedEvent = null; }
    }
    private void DrawCurrentTime()
    {
        foreach (var visual in currentTimeVisuals) Board.Children.Remove(visual);
        currentTimeVisuals.Clear();
        if (model is null || !double.IsFinite(Board.Width)) return;
        var now = DateTime.Now; var day = (now.Date - model.WeekStart).Days;
        if (day is < 0 or > 6) return;
        var x = DesktopPlanner.Application.WeekGeometry.TimeGutter + day * dayWidth;
        var y = now.TimeOfDay.TotalHours * hourHeight;
        var brush = new SolidColorBrush(Color.FromRgb(255, 130, 136));
        var line = new Line { X1 = x, X2 = x + dayWidth, Y1 = y, Y2 = y, Stroke = brush, StrokeThickness = 2, IsHitTestVisible = false };
        var dot = new Ellipse { Width = 7, Height = 7, Fill = brush, IsHitTestVisible = false };
        Canvas.SetLeft(dot, x - 3.5); Canvas.SetTop(dot, y - 3.5);
        currentTimeVisuals.Add(line); currentTimeVisuals.Add(dot);
        foreach (var visual in currentTimeVisuals) { Panel.SetZIndex(visual, 10); Board.Children.Add(visual); }
    }
}
