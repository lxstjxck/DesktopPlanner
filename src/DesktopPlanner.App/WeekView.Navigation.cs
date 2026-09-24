using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DesktopPlanner.Application;

namespace DesktopPlanner.App;

public partial class WeekView
{
    private bool navigating;
    private Point? swipeOrigin;
    private int? touchId;
    private double swipeDistance;

    private async void PreviousWeek(object sender, RoutedEventArgs e) => await NavigateWeekAsync(-1);
    private async void NextWeek(object sender, RoutedEventArgs e) => await NavigateWeekAsync(1);
    private async void GoToday(object sender, RoutedEventArgs e) => await NavigateWeekAsync(0);

    internal async Task NavigateWeekAsync(int direction)
    {
        var calendar = model;
        if (calendar is null || navigating) return;
        navigating = true;
        Navigation.IsEnabled = CalendarPage.IsHitTestVisible = false;
        PreviousButton.IsEnabled = NextButton.IsEnabled = false;
        var offset = Scroller.VerticalOffset;
        var sign = direction == 0 ? Math.Sign((WeekGeometry.Monday(DateTime.Today) - calendar.WeekStart).Days) : direction;
        try
        {
            if (sign != 0) await SlidePageAsync(-sign * Math.Max(1, CalendarPage.ActualWidth), 140);
            var command = direction == 0 ? calendar.TodayCommand : direction < 0 ? calendar.PreviousWeekCommand : calendar.NextWeekCommand;
            await command.ExecuteAsync(null);
            if (!IsLoaded) return;
            Scroller.ScrollToVerticalOffset(offset);
            if (sign != 0 && calendar.LastOperationSucceeded) PageShift.X = sign * Math.Max(1, CalendarPage.ActualWidth);
            await SlidePageAsync(0, 210);
        }
        finally
        {
            PageShift.BeginAnimation(TranslateTransform.XProperty, null); PageShift.X = 0;
            Navigation.IsEnabled = CalendarPage.IsHitTestVisible = true;
            PreviousButton.IsEnabled = NextButton.IsEnabled = true;
            navigating = false;
        }
    }

    private async Task SlidePageAsync(double target, int milliseconds)
    {
        if (!SystemParameters.ClientAreaAnimation) { PageShift.X = target; return; }
        var animation = new DoubleAnimation(PageShift.X, target, TimeSpan.FromMilliseconds(milliseconds))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        PageShift.BeginAnimation(TranslateTransform.XProperty, animation);
        await Task.Delay(milliseconds);
        PageShift.BeginAnimation(TranslateTransform.XProperty, null); PageShift.X = target;
    }

    private void BeginSwipe(Point point)
    {
        if (navigating) return;
        PageShift.BeginAnimation(TranslateTransform.XProperty, null);
        swipeOrigin = point; swipeDistance = 0;
    }
    private void UpdateSwipe(Point point)
    {
        if (swipeOrigin is not { } origin || navigating) return;
        var delta = point - origin;
        swipeDistance = Math.Abs(delta.X) > Math.Abs(delta.Y) ? delta.X : 0;
        PageShift.X = Math.Clamp(swipeDistance, -CalendarPage.ActualWidth * .4, CalendarPage.ActualWidth * .4);
    }
    private async Task EndSwipeAsync()
    {
        if (swipeOrigin is null) return;
        swipeOrigin = null;
        if (Math.Abs(swipeDistance) >= 60) await NavigateWeekAsync(swipeDistance < 0 ? 1 : -1);
        else { PageShift.BeginAnimation(TranslateTransform.XProperty, null); PageShift.X = 0; }
    }
    private void SwipeStart(object sender, MouseButtonEventArgs e)
    {
        if (navigating || touchId.HasValue || e.StylusDevice is not null) return;
        BeginSwipe(e.GetPosition(this)); SwipeHeader.CaptureMouse(); e.Handled = true;
    }
    private void SwipeMove(object sender, MouseEventArgs e)
    { if (SwipeHeader.IsMouseCaptured) UpdateSwipe(e.GetPosition(this)); }
    private async void SwipeEnd(object sender, MouseButtonEventArgs e)
    {
        if (!SwipeHeader.IsMouseCaptured) return;
        UpdateSwipe(e.GetPosition(this));
        var completion = EndSwipeAsync(); SwipeHeader.ReleaseMouseCapture();
        e.Handled = true; await completion;
    }
    private void SwipeCancel(object sender, MouseEventArgs e)
    {
        if (swipeOrigin is null) return;
        swipeOrigin = null; PageShift.X = 0;
    }
    private void TouchStart(object sender, TouchEventArgs e)
    {
        if (navigating || swipeOrigin.HasValue) return;
        touchId = e.TouchDevice.Id; BeginSwipe(e.GetTouchPoint(this).Position);
        e.TouchDevice.Capture(SwipeHeader); e.Handled = true;
    }
    private void MoveTouch(object sender, TouchEventArgs e)
    { if (touchId == e.TouchDevice.Id) { UpdateSwipe(e.GetTouchPoint(this).Position); e.Handled = true; } }
    private async void TouchEnd(object sender, TouchEventArgs e)
    {
        if (touchId != e.TouchDevice.Id) return;
        UpdateSwipe(e.GetTouchPoint(this).Position); touchId = null;
        e.TouchDevice.Capture(null); e.Handled = true; await EndSwipeAsync();
    }
}


