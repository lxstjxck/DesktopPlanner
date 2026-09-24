using DesktopPlanner.Domain;
namespace DesktopPlanner.Domain.Tests;
public sealed class DomainTests
{
    [Fact] public void CompletionAndRestorationKeepTimestampsConsistent()
    {
        var task = new TaskItem(); var now = new DateTime(2026, 9, 21, 10, 0, 0, DateTimeKind.Utc);
        task.Complete(now); Assert.True(task.IsCompleted); Assert.Equal(now, task.CompletedAt);
        task.Restore(now.AddMinutes(1)); Assert.False(task.IsCompleted); Assert.Null(task.CompletedAt); Assert.Equal(now.AddMinutes(1), task.UpdatedAt);
    }
    [Theory] [InlineData(0)] [InlineData(-1)]
    public void EventRejectsInvalidDuration(int minutes)
    { var item = new CalendarEvent(); var start = DateTime.UtcNow; Assert.Throws<ArgumentException>(() => item.SetPeriod(start, start.AddMinutes(minutes))); }
    [Theory] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)] [InlineData(.1)] [InlineData(2)]
    public void LayoutRejectsInvalidScale(double scale)
    { var layout = new WidgetLayout { Scale = scale }; Assert.Throws<ArgumentOutOfRangeException>(layout.Validate); }
    [Fact] public void NegativeMonitorCoordinatesAreValid()
    {
        var layout = new WidgetLayout { X = -1500, Y = -300, MonitorId = "left" };
        LayoutRecovery.Recover(layout, [new("left", -1920, -400, 1920, 1080, 1, false), new("main", 0, 0, 1920, 1080, 1, true)]);
        Assert.Equal(-1500, layout.X); Assert.Equal(-300, layout.Y);
    }
    [Fact] public void MissingMonitorRecoversToPrimaryWithDpi()
    {
        var layout = new WidgetLayout { X = -2000, Y = 2000, MonitorId = "unplugged", Width = 800, Height = 600 };
        LayoutRecovery.Recover(layout, [new("main", 0, 0, 1920, 1080, 1.5, true)]);
        Assert.Equal("main", layout.MonitorId); Assert.Equal(0, layout.X); Assert.Equal(180, layout.Y);
    }
    [Fact] public void OversizedLayoutFitsWorkingArea()
    {
        var layout = new WidgetLayout { Width = 2500, Height = 2000 };
        LayoutRecovery.Recover(layout, [new("main", 0, 0, 1920, 1080, 2, true)]);
        Assert.Equal(960, layout.Width); Assert.Equal(540, layout.Height);
    }
    [Fact] public void LayoutRejectsInvisibleOpacityAndInvalidDimensions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WidgetLayout { Opacity = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new WidgetLayout { X = double.NaN }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new WidgetLayout { Width = 20 }.Validate());
    }
}
