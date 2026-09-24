using DesktopPlanner.Domain;
namespace DesktopPlanner.Domain.Tests;
public sealed class DesktopPresentationTests
{
    [Fact] public void ReferenceLayoutHasWideCalendarAndThreeLowerPanels()
    {
        var layouts = ReferenceLayout.Create(new("primary", 0, 0, 1920, 1040, 1, true));
        var todo = layouts.Single(l => l.WidgetType == WidgetType.Todo);
        var week = layouts.Single(l => l.WidgetType == WidgetType.Week);
        var done = layouts.Single(l => l.WidgetType == WidgetType.Completed);
        var inbox = layouts.Single(l => l.WidgetType == WidgetType.Inbox);
        var notes = layouts.Single(l => l.WidgetType == WidgetType.Notes);
        Assert.Equal(todo.Y, week.Y); Assert.Equal(todo.Height, week.Height); Assert.True(week.Width > todo.Width * 2);
        Assert.Equal(done.Y, inbox.Y); Assert.Equal(inbox.Y, notes.Y); Assert.Equal(todo.X, done.X);
        Assert.Equal(week.X, inbox.X); Assert.Equal(week.X + week.Width, notes.X + notes.Width, 5);
        foreach (var layout in layouts) { layout.Validate(); Assert.True(layout.X >= 0 && layout.Y >= 0); }
    }
    [Fact] public void ReferenceLayoutUsesPhysicalCoordinatesAndDipSizes()
    {
        var layouts = ReferenceLayout.Create(new("left", -2560, -200, 2560, 1440, 1.5, false));
        foreach (var layout in layouts)
        {
            layout.Validate(); Assert.InRange(layout.X, -2560, 0);
            Assert.True(layout.X + layout.Width * 1.5 <= 0);
            Assert.True(layout.Y + layout.Height * 1.5 <= 1240);
        }
    }
    [Fact] public void MaximizedWorkAreaIsNotFullscreen()
    {
        var monitor = new ScreenRectangle(0, 0, 1920, 1080);
        Assert.False(new ScreenRectangle(0, 30, 1920, 1040).Covers(monitor));
        Assert.True(new ScreenRectangle(0, 0, 1920, 1080).Covers(monitor));
        Assert.True(new ScreenRectangle(-1, -1, 1921, 1081).Covers(monitor));
    }
    [Fact] public void FullscreenOnlySuppressesIntersectingMonitor()
    {
        var fullscreen = new ScreenRectangle(-1920, 0, 0, 1080);
        Assert.True(fullscreen.Intersects(new(-800, 100, -400, 500)));
        Assert.False(fullscreen.Intersects(new(0, 100, 400, 500)));
        Assert.True(fullscreen.Intersects(new(-100, 100, 300, 500)));
    }
}
