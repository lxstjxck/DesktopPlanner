using System.Runtime.InteropServices;
using System.Text;

namespace DesktopPlanner.Infrastructure;

public sealed partial class WindowsOverlayService
{
    public bool IsTopmost(nint window) => (ReadLong(window, -20) & 0x8) != 0;
    public bool IsToolWindow(nint window) => (ReadLong(window, -20) & 0x80) != 0;

    private delegate bool EnumWindowsCallback(nint window, nint data);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumWindows(EnumWindowsCallback callback, nint data);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int max);
}
