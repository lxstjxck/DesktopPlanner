using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using DesktopPlanner.Domain;
namespace DesktopPlanner.Infrastructure;

public sealed partial class WindowsOverlayService
{
    private readonly List<nint> eventHooks = [];
    private WinEventCallback? eventCallback;
    public event Action? DesktopCoverageChanged;
    public void StartDesktopTracking()
    {
        if (eventHooks.Count != 0) return;
        StartSwitcherTracking();
        eventCallback = (_, type, window, objectId, childId, _, _) =>
        {
            if (type == 0x14) SetWindowSwitching(true);
            if (type == 0x15) SetWindowSwitching(false);
            if (type < 0x8000 || (objectId == 0 && childId == 0)) DesktopCoverageChanged?.Invoke();
        };
        foreach (var range in new (uint First, uint Last)[] { (3, 3), (0x14, 0x17), (0x8001, 0x8004), (0x800A, 0x800B) })
        {
            // Skip our own windows so hiding/showing widgets cannot create a feedback loop.
            var hook = SetWinEventHook(range.First, range.Last, 0, eventCallback, 0, 0, 2);
            if (hook == 0) { StopDesktopTracking(); throw new Win32Exception(); }
            eventHooks.Add(hook);
        }
    }
    private void StopDesktopTracking()
    { StopSwitcherTracking(); foreach (var hook in eventHooks) UnhookWinEvent(hook); eventHooks.Clear(); eventCallback = null; }
    public IReadOnlyList<ScreenRectangle> GetDesktopObstructions(int? processId = null)
    {
        var result = new List<ScreenRectangle>();
        EnumWindowsCallback callback = (window, _) =>
        {
            if (!IsWindowVisible(window) || IsIconic(window)) return true;
            GetWindowThreadProcessId(window, out var process);
            if (process == Environment.ProcessId || (processId.HasValue && process != processId.Value)) return true;
            var name = new StringBuilder(256); GetClassName(window, name, name.Capacity);
            if (name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "SHELLDLL_DefView" or "SysShadow") return true;
            if (DwmGetWindowAttribute(window, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            if (!GetWindowRect(window, out var rect)) return true;
            if (rect.Right <= rect.Left || rect.Bottom <= rect.Top) return true;
            result.Add(new(rect.Left, rect.Top, rect.Right, rect.Bottom)); return true;
        };
        if (!EnumWindows(callback, 0)) throw new Win32Exception();
        return result;
    }
    public bool IntersectsWindow(nint window, ScreenRectangle bounds)
        => GetWindowRect(window, out var rect) && bounds.Intersects(new(rect.Left, rect.Top, rect.Right, rect.Bottom));


    public bool IsTopmost(nint window) => (GetStyle(window).ToInt64() & 8) != 0;
    private delegate bool EnumWindowsCallback(nint window, nint data);
    private delegate void WinEventCallback(nint hook, uint type, nint window, int objectId, int childId, uint thread, uint time);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumWindows(EnumWindowsCallback callback, nint data);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int max);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, uint attribute, out int value, int size);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWinEventHook(uint first, uint last, nint module, WinEventCallback callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
}



