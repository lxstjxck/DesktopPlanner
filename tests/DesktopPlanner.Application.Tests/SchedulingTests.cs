using DesktopPlanner.Application;
using DesktopPlanner.Domain;
namespace DesktopPlanner.Application.Tests;
public sealed class SchedulingTests
{
    [Theory] [InlineData(0, 0)] [InlineData(14, 0)] [InlineData(15, 0)] [InlineData(29, 0)] [InlineData(59, 30)]
    public void SnapsDownToHalfHour(int minute, int expected)
    {
        var start = new DateTime(2026, 9, 21, 9, minute, 37, DateTimeKind.Local);
        var snapped = SchedulingService.Snap(start);
        Assert.Equal(expected, snapped.Minute); Assert.Equal(0, snapped.Second); Assert.Equal(DateTimeKind.Local, snapped.Kind);
    }
    [Fact] public void ScheduleCreatesLinkedOneHourEventAcrossMidnight()
    {
        var task = new TaskItem { Title = "Plan" };
        var item = SchedulingService.Schedule(task, new DateTime(2026, 9, 21, 23, 59, 0));
        Assert.Equal(new DateTime(2026, 9, 21, 23, 30, 0), task.ScheduledStart);
        Assert.Equal(new DateTime(2026, 9, 22, 0, 30, 0), task.ScheduledEnd);
        Assert.Equal(item.Id, task.CalendarEventId); Assert.Same(task, item.Task); Assert.Equal("Plan", item.Title);
    }
    [Fact] public void ReschedulePreservesLinkedEventId()
    {
        var task = new TaskItem(); var item = SchedulingService.Schedule(task, DateTime.Today);
        var moved = SchedulingService.Schedule(task, DateTime.Today.AddHours(12), TimeSpan.FromMinutes(30), item);
        Assert.Same(item, moved); Assert.Equal(TimeSpan.FromMinutes(30), moved.End - moved.Start);
        Assert.Equal(moved.End, task.ScheduledEnd);
    }
    [Fact] public void RejectsDuplicateEventAndInvalidDuration()
    {
        var task = new TaskItem(); SchedulingService.Schedule(task, DateTime.Today);
        Assert.Throws<InvalidOperationException>(() => SchedulingService.Schedule(task, DateTime.Today));
        Assert.Throws<ArgumentOutOfRangeException>(() => SchedulingService.Schedule(new(), DateTime.Today, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => SchedulingService.Snap(DateTime.Today, 0));
    }
}
