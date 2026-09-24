using DesktopPlanner.Application;
using DesktopPlanner.Domain;
namespace DesktopPlanner.Application.Tests;
public sealed class WeekGeometryTests
{
    [Theory] [InlineData(21, 21)] [InlineData(27, 21)] [InlineData(28, 28)]
    public void WeekBeginsOnMonday(int day, int expected)
        => Assert.Equal(new DateTime(2026, 9, expected), WeekGeometry.Monday(new DateTime(2026, 9, day)));
    [Theory] [InlineData(0, 0, 0, 0)] [InlineData(150, 557, 1, 555)] [InlineData(699, 1440, 6, 1425)]
    public void PointerMapsToDayAndSnappedTime(double x, double y, int day, int minute)
    {
        var monday = new DateTime(2026, 9, 21);
        Assert.Equal(monday.AddDays(day).AddMinutes(minute), WeekGeometry.TimeAt(monday, x, y, 100));
    }
    [Fact] public void OvernightEventIsSplitAndOnlyLastSegmentResizes()
    {
        var monday = new DateTime(2026, 9, 21);
        var item = new CalendarEvent { Start = monday.AddHours(23.5), End = monday.AddDays(1).AddHours(1) };
        var segments = WeekGeometry.Arrange([item], monday);
        Assert.Equal(2, segments.Count); Assert.Equal(30, segments[0].Height); Assert.Equal(60, segments[1].Height);
        Assert.False(segments[0].CanResize); Assert.True(segments[1].CanResize);
    }
    [Fact] public void OverlapsUseSeparateColumnsButTouchingEventsDoNot()
    {
        var monday = new DateTime(2026, 9, 21);
        var a = new CalendarEvent { Start = monday.AddHours(9), End = monday.AddHours(10) };
        var b = new CalendarEvent { Start = monday.AddHours(9.5), End = monday.AddHours(11) };
        var c = new CalendarEvent { Start = monday.AddHours(11), End = monday.AddHours(12) };
        var segments = WeekGeometry.Arrange([a, b, c], monday);
        Assert.Equal(2, segments[0].ColumnCount); Assert.Equal(2, segments[1].ColumnCount);
        Assert.NotEqual(segments[0].Column, segments[1].Column); Assert.Equal(1, segments[2].ColumnCount);
    }
    [Fact] public void WeekClipsAnEventStartingBeforeMonday()
    {
        var monday = new DateTime(2026, 9, 21);
        var segments = WeekGeometry.Arrange([new CalendarEvent { Start = monday.AddHours(-1), End = monday.AddHours(1) }], monday);
        Assert.Single(segments); Assert.Equal(0, segments[0].Top); Assert.Equal(60, segments[0].Height);
    }
}
