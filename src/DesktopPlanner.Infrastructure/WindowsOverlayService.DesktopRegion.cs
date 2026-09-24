using System.Runtime.InteropServices;
namespace DesktopPlanner.Infrastructure;
public sealed partial class WindowsOverlayService
{
    private readonly HashSet<nint> desktopWindows = [];
    private readonly Dictionary<nint, (int Width, int Height)> roundedSizes = [];
    public void UpdateDesktopRegion()
    {
        desktopWindows.RemoveWhere(window => !IsWindow(window));
        foreach (var window in desktopWindows)
        {
            if (!GetWindowRect(window, out var r)) continue;
            var size = (r.Right - r.Left, r.Bottom - r.Top);
            if (roundedSizes.TryGetValue(window, out var previous) && previous == size) continue;
            var region = CreateRoundRectRgn(8, 8, size.Item1 - 7, size.Item2 - 7, 56, 56);
            if (SetWindowRgn(window, region, true) == 0) DeleteObject(region);
            else roundedSizes[window] = size;
        }
    }
    private void RestoreDesktopRegion() { desktopWindows.Clear(); roundedSizes.Clear(); }
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(nint window, nint region, bool redraw);
    [DllImport("gdi32.dll")] private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
}