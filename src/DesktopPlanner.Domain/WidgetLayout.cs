namespace DesktopPlanner.Domain;

public enum WidgetType { Todo, Week, Completed, Inbox, Notes }
// X/Y are physical virtual-desktop pixels; Width/Height are WPF device-independent units.
public sealed class WidgetLayout
{
    public int Id { get; set; }
    public WidgetType WidgetType { get; set; }
    public double X { get; set; } = 60;
    public double Y { get; set; } = 60;
    public double Width { get; set; } = 340;
    public double Height { get; set; } = 360;
    public double Scale { get; set; } = 1;
    public double Opacity { get; set; } = .94;
    public string MonitorId { get; set; } = "";
    public bool IsVisible { get; set; } = true;
    public bool IsPositionLocked { get; set; }
    public void Validate()
    {
        if (!double.IsFinite(X) || !double.IsFinite(Y) || !double.IsFinite(Width) || !double.IsFinite(Height)
            || Width < 260 || Height < 180 || !double.IsFinite(Scale) || Scale < .75 || Scale > 1.75
            || !double.IsFinite(Opacity) || Opacity < .3 || Opacity > 1)
            throw new ArgumentOutOfRangeException(nameof(WidgetLayout), "Invalid widget geometry or appearance.");
    }
}
public sealed record MonitorArea(string Id, double X, double Y, double Width, double Height, double DpiScale, bool IsPrimary);
public static class LayoutRecovery
{
    public static void Recover(WidgetLayout layout, IReadOnlyList<MonitorArea> monitors)
    {
        layout.Validate();
        if (monitors.Count == 0) return;
        var monitor = monitors.FirstOrDefault(m => m.Id == layout.MonitorId)
            ?? monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
        layout.Width = Math.Min(layout.Width, Math.Max(260, monitor.Width / monitor.DpiScale));
        layout.Height = Math.Min(layout.Height, Math.Max(180, monitor.Height / monitor.DpiScale));
        layout.X = Math.Clamp(layout.X, monitor.X, monitor.X + Math.Max(0, monitor.Width - layout.Width * monitor.DpiScale));
        layout.Y = Math.Clamp(layout.Y, monitor.Y, monitor.Y + Math.Max(0, monitor.Height - layout.Height * monitor.DpiScale));
        layout.MonitorId = monitor.Id;
    }
}
