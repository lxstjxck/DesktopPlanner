namespace DesktopPlanner.Domain;

public static class ReferenceLayout
{
    public const string Version = "glass-dashboard-v1";
    public static IReadOnlyList<WidgetLayout> Create(MonitorArea monitor)
    {
        const double gap = 12;
        var availableWidth = monitor.Width / monitor.DpiScale;
        var availableHeight = monitor.Height / monitor.DpiScale;
        var width = Math.Min(availableWidth - 24, Math.Max(980, availableWidth * .76));
        var height = Math.Min(availableHeight - 24, Math.Max(600, availableHeight * .9));
        var left = Math.Max(260, width * .25);
        var right = width - left - gap;
        var top = Math.Max(180, (height - gap) * .68);
        var bottom = Math.Max(180, height - top - gap);
        var x = monitor.X + (availableWidth - width) / 2 * monitor.DpiScale;
        var y = monitor.Y + (availableHeight - (top + bottom + gap)) / 2 * monitor.DpiScale;
        WidgetLayout Make(WidgetType type, double dx, double dy, double w, double h) => new()
        { WidgetType = type, X = x + dx * monitor.DpiScale, Y = y + dy * monitor.DpiScale,
            Width = Math.Max(260, w), Height = h, MonitorId = monitor.Id, Opacity = 1 };
        return [Make(WidgetType.Todo, 0, 0, left, top), Make(WidgetType.Week, left + gap, 0, right, top),
            Make(WidgetType.Completed, 0, top + gap, left, bottom),
            Make(WidgetType.Inbox, left + gap, top + gap, (right - gap) * .48, bottom),
            Make(WidgetType.Notes, left + gap + (right - gap) * .48 + gap, top + gap, (right - gap) * .52, bottom)];
    }
}
public readonly record struct ScreenRectangle(double Left, double Top, double Right, double Bottom)
{
    public bool Covers(ScreenRectangle other, double tolerance = 1) => Left <= other.Left + tolerance && Top <= other.Top + tolerance
        && Right >= other.Right - tolerance && Bottom >= other.Bottom - tolerance;
    public bool Intersects(ScreenRectangle other) => Left < other.Right && Right > other.Left && Top < other.Bottom && Bottom > other.Top;
}
