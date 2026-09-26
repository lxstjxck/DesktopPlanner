using System.ComponentModel;
using System.Runtime.InteropServices;
using DesktopPlanner.Domain;
namespace DesktopPlanner.Infrastructure;

public sealed partial class WindowsOverlayService : IDisposable
{
    private nint hotkeyWindow;
    public event Action? InteractionToggleRequested;
    public bool RegisterShortcut(nint handle, uint modifiers = 0x0002 | 0x0004, uint key = 0x20)
    {
        if (hotkeyWindow != 0) UnregisterHotKey(hotkeyWindow, 1);
        hotkeyWindow = 0;
        if (!RegisterHotKey(handle, 1, modifiers | 0x4000, key)) return false;
        hotkeyWindow = handle; return true;
    }
    public bool ProcessMessage(int message, nint wParam)
    {
        if (message != 0x0312 || wParam != 1) return false;
        InteractionToggleRequested?.Invoke(); return true;
    }
    public void SetInteractionLock(nint handle, bool locked)
    {
        const long mask = 0x20 | 0x08000000; // WS_EX_TRANSPARENT | WS_EX_NOACTIVATE
        var style = GetStyle(handle).ToInt64();
        var next = locked ? style | mask : style & ~mask;
        Marshal.SetLastPInvokeError(0);
        var result = IntPtr.Size == 8 ? SetWindowLongPtr(handle, -20, (nint)next) : (nint)SetWindowLong(handle, -20, (int)next);
        if (result == 0 && Marshal.GetLastPInvokeError() != 0) throw new Win32Exception();
        if (!EnableWindow(handle, !locked)) { /* Return value is previous enabled state, not success. */ }
        if (!SetWindowPos(handle, IsDesktopChild(handle) ? 0 : new nint(-2), 0, 0, 0, 0,
            0x0001 | 0x0002 | 0x0010 | 0x0020 | (IsDesktopOwned(handle) ? 0x0004u : 0u))) throw new Win32Exception();
    }
    public void Place(nint handle, double x, double y)
    {
        var point = new NativePoint { X = (int)Math.Round(x), Y = (int)Math.Round(y) };
        var attached = IsDesktopChild(handle);
        if (attached) ScreenToClient(DesktopParent, ref point);
        if (!SetWindowPos(handle, attached ? 0 : new nint(-2), point.X, point.Y, 0, 0,
            0x0001 | 0x0010 | (IsDesktopOwned(handle) ? 0x0004u : 0u))) throw new Win32Exception();
    }
    public (double X, double Y, string MonitorId) GetPosition(nint handle)
    {
        if (!GetWindowRect(handle, out var rect)) throw new Win32Exception();
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(handle, 2), ref info)) throw new Win32Exception();
        return (rect.Left, rect.Top, info.Device);
    }
    public IReadOnlyList<MonitorArea> GetMonitors()
    {
        var result = new List<MonitorArea>();
        MonitorCallback callback = (nint monitor, nint dc, ref Rect rect, nint data) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info)) return true;
            var hr = GetDpiForMonitor(monitor, 0, out var x, out _);
            var scale = hr == 0 ? x / 96d : 1;
            result.Add(new MonitorArea(info.Device, info.Work.Left, info.Work.Top,
                info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top, scale, (info.Flags & 1) != 0));
            return true;
        };
        if (!EnumDisplayMonitors(0, 0, callback, 0)) throw new Win32Exception();
        return result;
    }
    private static nint GetStyle(nint h) => IntPtr.Size == 8 ? GetWindowLongPtr(h, -20) : GetWindowLong(h, -20);
    public void Dispose() { RestoreDesktopRegion(); if (hotkeyWindow != 0) UnregisterHotKey(hotkeyWindow, 1); hotkeyWindow = 0; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MonitorInfo
    { public int Size; public Rect Monitor, Work; public uint Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device; }
    private delegate bool MonitorCallback(nint monitor, nint dc, ref Rect rect, nint data);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint handle, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(nint handle, out Rect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint handle, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint handle, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern nint SetWindowLongPtr(nint handle, int index, nint value);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)] private static extern int SetWindowLong(nint handle, int index, int value);
    [DllImport("user32.dll")] private static extern bool EnableWindow(nint handle, bool enabled);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint handle, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint handle, int id);
}


