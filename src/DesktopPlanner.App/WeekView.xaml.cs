using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using DesktopPlanner.Application;
namespace DesktopPlanner.App;

// Only WPF geometry, pointer gestures and rendering live here; mutations go through CalendarViewModel.
public partial class WeekView : UserControl
{
    private CalendarViewModel? model;
    private Border? dropPreview;
    private double hourHeight = 20;
    private double DayHeight => hourHeight * 24;
    private bool detailed = true;
    private double scrollTarget;
    private Line? pointerLine;
    private double dayWidth = WeekGeometry.MinimumDayWidth;
    private static readonly Brush GridBrush = new SolidColorBrush(Color.FromArgb(40, 239, 247, 255));
    public WeekView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => AttachModel();
        Loaded += RestoreViewport;
        Unloaded += (_, _) => { clockTimer.Stop(); viewportTimer.Stop(); if (model is not null) { _ = model.SaveViewportAsync(); model.PropertyChanged -= ModelChanged; } model = null; BeginAnimation(SmoothOffsetProperty, null); };
        clockTimer.Tick += (_, _) => DrawCurrentTime();
        viewportTimer.Tick += async (_, _) => { viewportTimer.Stop(); if (model is not null) await model.SaveViewportAsync(); };
    }
    private void AttachModel()
    {
        if (model is not null) model.PropertyChanged -= ModelChanged;
        model = DataContext as CalendarViewModel;
        if (model is not null) model.PropertyChanged += ModelChanged;
        Render();
    }
    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CalendarViewModel.WeekStart)) selectedEvent = null;
        if (e.PropertyName is nameof(CalendarViewModel.Events) or nameof(CalendarViewModel.WeekStart)) Render();
        if (e.PropertyName == nameof(CalendarViewModel.NavigationTarget)) QueueTaskNavigation();
    }
    private void Scrolled(object sender, ScrollChangedEventArgs e)
    {
        if (!restoringViewport && model is not null && e.VerticalChange != 0) RememberViewport();
        Days.RenderTransform = new TranslateTransform(-Scroller.HorizontalOffset, 0);
        if (dropPreview is null && (pointerLine is null || !Board.Children.Contains(pointerLine)))
        {
            var from = TimeSpan.FromHours(Scroller.VerticalOffset / hourHeight);
            var to = TimeSpan.FromHours(Math.Min(24, (Scroller.VerticalOffset + Scroller.ViewportHeight) / hourHeight));
            RangeLabel.Text = $"{from.Hours:00}:{from.Minutes:00} — {(int)to.TotalHours:00}:{to.Minutes:00} · шаг 30 мин";
        }
        if (e.ViewportWidthChange != 0 || e.ViewportHeightChange != 0) Render();
    }
    public static readonly DependencyProperty SmoothOffsetProperty = DependencyProperty.Register(
        "SmoothOffset", typeof(double), typeof(WeekView), new PropertyMetadata(0d, (d, e) => ((WeekView)d).Scroller.ScrollToVerticalOffset((double)e.NewValue)));
    private void AnimateScroll(double target)
    {
        scrollTarget = Math.Clamp(target, 0, Scroller.ScrollableHeight);
        var from = Scroller.VerticalOffset;
        BeginAnimation(SmoothOffsetProperty, null); SetValue(SmoothOffsetProperty, from);
        BeginAnimation(SmoothOffsetProperty, new DoubleAnimation(from, scrollTarget, TimeSpan.FromMilliseconds(190))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }
    private void SmoothWheel(object sender, MouseWheelEventArgs e)
    {
        if (Scroller.ScrollableHeight <= 0) return;
        if (!HasAnimatedProperties) scrollTarget = Scroller.VerticalOffset;
        AnimateScroll(scrollTarget - e.Delta * .65); e.Handled = true;
    }
    private void StopSmoothScroll(object sender, MouseButtonEventArgs e)
    { var offset = Scroller.VerticalOffset; BeginAnimation(SmoothOffsetProperty, null); SetValue(SmoothOffsetProperty, offset); scrollTarget = offset; }
    private void ShowPointerTime(object sender, MouseEventArgs e)
    {
        if (model is null || e.LeftButton == MouseButtonState.Pressed) return;
        var point = e.GetPosition(Board);
        if (point.X < WeekGeometry.TimeGutter) return;
        var time = WeekGeometry.TimeAt(model.WeekStart, point.X - WeekGeometry.TimeGutter, point.Y / hourHeight * WeekGeometry.HourHeight, dayWidth);
        RangeLabel.Text = time.ToString("ddd, dd MMM · HH:mm", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));
        pointerLine ??= new Line { Stroke = new SolidColorBrush(Color.FromArgb(155, 184, 222, 255)), StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 3 }, IsHitTestVisible = false };
        if (!Board.Children.Contains(pointerLine)) Board.Children.Add(pointerLine);
        pointerLine.X1 = WeekGeometry.TimeGutter; pointerLine.X2 = Board.Width;
        pointerLine.Y1 = pointerLine.Y2 = time.TimeOfDay.TotalHours * hourHeight;
    }
    private void ClearPointerTime(object sender, MouseEventArgs e)
    { if (pointerLine is not null) Board.Children.Remove(pointerLine); }
    private void ToggleDensity(object sender, RoutedEventArgs e)
    {
        detailed = !detailed; DensityButton.Content = detailed ? "Сутки" : "По часам";
        BeginAnimation(SmoothOffsetProperty, null); Render(); Scroller.ScrollToVerticalOffset(detailed ? 8 * hourHeight : 0);
        if (model is not null) { model.SetViewport(detailed ? 8 : 0, detailed); viewportTimer.Start(); }
    }
    private void ViewportChanged(object sender, SizeChangedEventArgs e) => Render();
    private void Render()
    {
        if (model is null || Board is null) return;
        if (model.Events.Count == 0)
        {
            selectedEvent = null; appliedNavigation = null;
            if (EditorOverlay.Visibility == Visibility.Visible) CloseEditor();
        }
        var width = Math.Max(WeekGeometry.TimeGutter + 7 * 56, Scroller.ViewportWidth);
        if (!double.IsFinite(width)) return;
        hourHeight = detailed ? 48 : Math.Max(14, (Scroller.ViewportHeight - 1) / 24);
        Board.Height = DayHeight;
        Surface.Width = width; Board.Width = width; Days.Width = width;
        dayWidth = (width - WeekGeometry.TimeGutter) / 7;
        Board.Children.Clear(); Days.Children.Clear(); dropPreview = null; pointerLine = null;
        for (var day = 0; day < 7; day++)
        {
            var date = model.WeekStart.AddDays(day);
            var text = new TextBlock { Text = date.ToString("ddd", System.Globalization.CultureInfo.GetCultureInfo("ru-RU")) + "\n" + date.Day, TextAlignment = TextAlignment.Center, Width = dayWidth, FontSize = 12,
                Foreground = date == DateTime.Today ? Brushes.White : Brushes.White, Margin = new Thickness(0, 3, 0, 0) };
            if (date == DateTime.Today)
            {
                var pill = new Border { Width = 32, Height = 38, CornerRadius = new CornerRadius(14), Background = new SolidColorBrush(Color.FromArgb(115, 119, 171, 236)) };
                Canvas.SetLeft(pill, WeekGeometry.TimeGutter + day * dayWidth + (dayWidth - 32) / 2); Days.Children.Add(pill);
            }
            Canvas.SetLeft(text, WeekGeometry.TimeGutter + day * dayWidth); Days.Children.Add(text);
            if (date == DateTime.Today)
            {
                var today = new Rectangle { Width = dayWidth, Height = DayHeight, Fill = new SolidColorBrush(Color.FromArgb(16, 102, 171, 255)), IsHitTestVisible = false };
                Canvas.SetLeft(today, WeekGeometry.TimeGutter + day * dayWidth); Board.Children.Add(today);
            }
        }
        for (var i = 0; i <= 7; i++) Line(WeekGeometry.TimeGutter + i * dayWidth, 0, WeekGeometry.TimeGutter + i * dayWidth, DayHeight);
        for (var halfHour = 0; halfHour <= 48; halfHour++)
        {
            var y = halfHour * hourHeight / 2;
            Line(WeekGeometry.TimeGutter, y, width, y, halfHour % 2 == 0 ? .75 : .2);
            if (halfHour % 2 == 0 && halfHour < 48)
            {
                var label = new TextBlock { Text = $"{halfHour / 2:00}:00", FontSize = detailed ? 12 : 9, Opacity = .85 };
                Canvas.SetLeft(label, 1); Canvas.SetTop(label, Math.Min(DayHeight - 14, y + 2)); Board.Children.Add(label);
            }
        }
        foreach (var segment in WeekGeometry.Arrange(model.Events, model.WeekStart)) AddEvent(segment);
        DrawCurrentTime();
    }
    private void Line(double x1, double y1, double x2, double y2, double opacity = 1)
        => Board.Children.Add(new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = GridBrush, StrokeThickness = 1, Opacity = opacity, IsHitTestVisible = false });
    private void AddEvent(EventSegment segment)
    {
        var item = segment.Event;
        var eventColor = ParseEventColor(item.ColorHex);
        var segmentHeight = segment.Height / WeekGeometry.HourHeight * hourHeight;
        var segmentTop = segment.Top / WeekGeometry.HourHeight * hourHeight;
        var blockWidth = Math.Max(12, dayWidth / segment.ColumnCount - 4);
        var block = new Border { Width = blockWidth, Height = Math.Max(9, segmentHeight - 1), CornerRadius = new CornerRadius(5),
            BorderBrush = new SolidColorBrush(Color.FromArgb(item.Id == selectedEvent ? (byte)255 : (byte)100, 242, 231, 255)), BorderThickness = new Thickness(item.Id == selectedEvent ? 2 : 1),
            Background = new SolidColorBrush(Color.FromArgb(item.ColorHex is null ? (byte)125 : (byte)220, eventColor.R, eventColor.G, eventColor.B)), ClipToBounds = true,
            ToolTip = $"{item.Title}\n{item.Start:dd.MM HH:mm} — {item.End:dd.MM HH:mm}\n{item.Description}" };
        Canvas.SetTop(block, segmentTop);
        Canvas.SetLeft(block, WeekGeometry.TimeGutter + segment.Day * dayWidth + segment.Column * dayWidth / segment.ColumnCount + 2);
        var content = new Grid(); block.Child = content;
        var label = new TextBlock { Text = $"{item.Start:HH:mm} {item.Title}", TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = detailed ? 12 : 9, Margin = new Thickness(4, 2, 3, 1), Foreground = item.ColorHex is not null && (.2126 * eventColor.R + .7152 * eventColor.G + .0722 * eventColor.B) > 150 ? new SolidColorBrush(Color.FromRgb(18, 28, 43)) : Brushes.White };
        var text = new Grid { Margin = new Thickness(0, 0, 0, segment.CanResize ? 6 : 1), IsHitTestVisible = false, ClipToBounds = true };
        text.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        text.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        text.Children.Add(label); content.Children.Add(text);
        if (!string.IsNullOrWhiteSpace(item.Description))
        {
            label.MaxHeight = Math.Max(12, (block.Height - 8) / 2);
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            var description = new TextBlock { Text = item.Description.Trim(), TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis, FontSize = detailed ? 11 : 9,
                Foreground = label.Foreground, Margin = new Thickness(4, 0, 3, 0) };
            Grid.SetRow(description, 1); text.Children.Add(description);
        }
        var menu = new ContextMenu();
        var edit = new MenuItem { Header = item.IsReadOnly ? "Повторы и приглашения — только просмотр" : "Редактировать…", IsEnabled = !item.IsReadOnly };
        edit.Click += (_, _) => OpenEditor(item); menu.Items.Add(edit);
        menu.Items.Add(CreateColorMenu(item));
        var delete = new MenuItem { Header = "Удалить событие (задача останется)", IsEnabled = !item.IsReadOnly };
        delete.Click += async (_, _) => { if (model is not null) await model.DeleteAsync(item.Id); };
        menu.Items.Add(delete); block.ContextMenu = menu;
        var origin = new Point(); var pressed = false;
        block.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (IsThumb(e.OriginalSource as DependencyObject)) return;
            selectedEvent = item.Id; Board.Focus();
            if (e.ClickCount == 2) { pressed = false; e.Handled = true; OpenEditor(item); return; }
            origin = e.GetPosition(block); pressed = !item.IsAllDay && !item.IsReadOnly;
        };
        block.MouseMove += (_, e) =>
        {
            if (!pressed || e.LeftButton != MouseButtonState.Pressed) return;
            var point = e.GetPosition(block);
            if (Math.Abs(point.X - origin.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - origin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            pressed = false;
            var offset = (segment.Start - item.Start).TotalMinutes + origin.Y / hourHeight * 60;
            DragDrop.DoDragDrop(block, new DataObject(typeof(CalendarDrag), new CalendarDrag(CalendarSource.Event, item.Id, offset)), DragDropEffects.Move);
        };
        if (segment.CanResize && !item.IsReadOnly)
        {
            var thumb = new Thumb { Style = (Style)FindResource("EventResizeGrip"), Height = 6, VerticalAlignment = VerticalAlignment.Bottom, Cursor = Cursors.SizeNS, Opacity = .65 };
            content.Children.Add(thumb);
            double delta = 0;
            thumb.DragStarted += (_, _) => { delta = 0; pressed = false; };
            thumb.DragDelta += (_, e) =>
            {
                delta += e.VerticalChange; block.Height = Math.Max(9, segmentHeight + delta - 1);
                var minutes = Math.Round(delta / hourHeight * 60 / 30, MidpointRounding.AwayFromZero) * 30;
                var end = item.End.AddMinutes(minutes); if (end < item.Start.AddMinutes(30)) end = item.Start.AddMinutes(30);
                RangeLabel.Text = $"{item.Start:HH:mm} → {end:HH:mm} · {(end - item.Start).TotalMinutes:0} мин";
                label.Text = $"{item.Start:HH:mm}–{end:HH:mm}\n{item.Title}";
            };
            thumb.DragCompleted += async (_, e) =>
            {
                var minutes = Math.Round(delta / hourHeight * 60 / 30, MidpointRounding.AwayFromZero) * 30;
                var end = item.End.AddMinutes(minutes);
                if (end < item.Start.AddMinutes(30)) end = item.Start.AddMinutes(30);
                block.Height = Math.Max(9, segmentHeight - 1);
                if (!e.Canceled && model is not null && end != item.End) await model.ResizeAsync(item.Id, end);
                else Render();
            };
        }
        Board.Children.Add(block);
    }
    private static bool IsThumb(DependencyObject? source)
    {
        while (source is not null) { if (source is Thumb) return true; source = VisualTreeHelper.GetParent(source); }
        return false;
    }
    private bool TryDrop(DragEventArgs e, out CalendarDrag? drag, out DateTime time)
    {
        drag = e.Data.GetData(typeof(CalendarDrag)) as CalendarDrag; time = default;
        if (model is null || drag is null) return false;
        var point = e.GetPosition(Board);
        if (point.X < WeekGeometry.TimeGutter || point.X >= Board.Width || point.Y < 0 || point.Y > DayHeight) return false;
        time = WeekGeometry.TimeAt(model.WeekStart, point.X - WeekGeometry.TimeGutter, point.Y / hourHeight * WeekGeometry.HourHeight, dayWidth)
            .AddMinutes(-Math.Floor(drag.GrabOffsetMinutes / 30) * 30);
        return true;
    }
    private void CalendarDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true; e.Effects = DragDropEffects.None;
        if (!TryDrop(e, out var drag, out var time)) { RemovePreview(); return; }
        e.Effects = DragDropEffects.Move;
        var point = e.GetPosition(Board);
        var duration = drag!.Source == CalendarSource.Event ? (model!.Events.FirstOrDefault(item => item.Id == drag.Id) is { } item ? item.End - item.Start : TimeSpan.FromHours(1)) : TimeSpan.FromHours(1);
        var end = time.Add(duration);
        RangeLabel.Text = $"{time:ddd dd.MM} · {time:HH:mm} → {end:HH:mm} · {duration.TotalMinutes:0} мин";
        if (dropPreview is null)
        {
            dropPreview = new Border { CornerRadius = new CornerRadius(9), BorderBrush = Brushes.LightSkyBlue, BorderThickness = new Thickness(1.5), Background = new SolidColorBrush(Color.FromArgb(115, 88, 159, 229)), IsHitTestVisible = false,
                Child = new TextBlock { Foreground = Brushes.White, FontSize = 12, Margin = new Thickness(5), TextWrapping = TextWrapping.Wrap } };
            Board.Children.Add(dropPreview);
        }
        var day = Math.Clamp((int)((point.X - WeekGeometry.TimeGutter) / dayWidth), 0, 6);
        var dayStart = model!.WeekStart.AddDays(day);
        var visibleStart = time < dayStart ? dayStart : time;
        var visibleEnd = end > dayStart.AddDays(1) ? dayStart.AddDays(1) : end;
        dropPreview.Width = dayWidth - 4; dropPreview.Height = Math.Max(22, (visibleEnd - visibleStart).TotalHours * hourHeight);
        ((TextBlock)dropPreview.Child).Text = $"{time:HH:mm}–{end:HH:mm}";
        var targetTop = visibleStart.TimeOfDay.TotalHours * hourHeight;
        var oldTop = Canvas.GetTop(dropPreview); if (!double.IsFinite(oldTop)) oldTop = targetTop;
        Canvas.SetLeft(dropPreview, WeekGeometry.TimeGutter + day * dayWidth + 2);
        Canvas.SetTop(dropPreview, targetTop);
        dropPreview.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(oldTop, targetTop, TimeSpan.FromMilliseconds(90)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });        // Edge scrolling is driven by drag events, not a UI polling loop.
        var visible = e.GetPosition(Scroller);
        if (visible.Y < 35) AnimateScroll(Scroller.VerticalOffset - 35);
        else if (visible.Y > Scroller.ActualHeight - 35) AnimateScroll(Scroller.VerticalOffset + 35);
        if (visible.X < 25) Scroller.ScrollToHorizontalOffset(Scroller.HorizontalOffset - 10);
        else if (visible.X > Scroller.ActualWidth - 25) Scroller.ScrollToHorizontalOffset(Scroller.HorizontalOffset + 10);
    }
    private void CalendarDragLeave(object sender, DragEventArgs e) => RemovePreview();
    private void RemovePreview() { if (dropPreview is not null) Board.Children.Remove(dropPreview); dropPreview = null; }
    private async void CalendarDrop(object sender, DragEventArgs e)
    {
        RemovePreview(); e.Handled = true; e.Effects = DragDropEffects.None;
        if (TryDrop(e, out var drag, out var time) && model is not null)
        { await model.ScheduleAsync(drag!, time); if (model.LastOperationSucceeded) e.Effects = DragDropEffects.Move; }
    }
}






