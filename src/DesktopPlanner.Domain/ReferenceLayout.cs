namespace DesktopPlanner.Domain;

public static class ReferenceLayout
{
    public const string Version = "glass-dashboard-v2";
    public static IReadOnlyList<WidgetLayout> Create(MonitorArea monitor)
    {
        const double gap = 12;
        var availableWidth = monitor.Width / monitor.DpiScale;
        var availableHeight = monitor.Height / monitor.DpiScale;
        var width = Math.Min(availableWidth - 24, Math.Max(980, availableWidth * .76));
        var height = Math.Min(availableHeight - 24, Math.Max(600, availableHeight * .9));
        var todoWidth = Math.Max(260, width * .22);
        // The tracker is a month calendar: reserve a nearly square surface for its
        // seven-day grid instead of stretching it to the full dashboard height.
        var trackerWidth = Math.Min(Math.Max(320, width * .28), width - todoWidth - 438 - gap * 2);
        var weekWidth = width - todoWidth - trackerWidth - gap * 2;
        var top = Math.Max(180, (height - gap) * .68);
        var bottom = Math.Max(180, height - top - gap);
        var trackerHeight = Math.Min(top, Math.Max(300, trackerWidth * 1.05));
        var x = monitor.X + (availableWidth - width) / 2 * monitor.DpiScale;
        var y = monitor.Y + (availableHeight - (top + bottom + gap)) / 2 * monitor.DpiScale;
        WidgetLayout Make(WidgetType type, double dx, double dy, double w, double h) => new()
        { WidgetType = type, X = x + dx * monitor.DpiScale, Y = y + dy * monitor.DpiScale,
            Width = Math.Max(260, w), Height = h, MonitorId = monitor.Id, Opacity = 1 };
        var trackerX = todoWidth + gap;
        var weekX = trackerX + trackerWidth + gap;
        var lowerWidth = width - todoWidth - gap;
        return [Make(WidgetType.Todo, 0, 0, todoWidth, top),
            Make(WidgetType.MonthTracker, trackerX, 0, trackerWidth, trackerHeight),
            Make(WidgetType.Week, weekX, 0, weekWidth, top),
            Make(WidgetType.Completed, 0, top + gap, todoWidth, bottom),
            Make(WidgetType.Inbox, trackerX, top + gap, (lowerWidth - gap) * .48, bottom),
            Make(WidgetType.Notes, trackerX + (lowerWidth - gap) * .48 + gap, top + gap, (lowerWidth - gap) * .52, bottom)];
    }
}
public readonly record struct ScreenRectangle(double Left, double Top, double Right, double Bottom)
{
    public bool Covers(ScreenRectangle other, double tolerance = 1) => Left <= other.Left + tolerance && Top <= other.Top + tolerance
        && Right >= other.Right - tolerance && Bottom >= other.Bottom - tolerance;
    public bool Intersects(ScreenRectangle other) => Left < other.Right && Right > other.Left && Top < other.Bottom && Bottom > other.Top;
}
