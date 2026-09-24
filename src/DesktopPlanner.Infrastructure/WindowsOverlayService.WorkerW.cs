using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace DesktopPlanner.Infrastructure;

public sealed partial class WindowsOverlayService
{
    private nint worker;
    // Windows 11's raised desktop requires layered siblings above WorkerW under Progman.
    // Older Explorer versions accept children of WorkerW directly.
    private nint DesktopParent => GetParent(worker) == FindWindow("Progman", null) ? GetParent(worker) : worker;
    private DateTime nextHostAttempt;
    public bool IsSwitchingWindows { get; private set; }
    public void PreserveDesktopLayer(nint window, int message, nint parameter, nint data)
    {
        if (unchecked((int)parameter) != -20 || !IsDesktopChild(window)) return;
        if (message == 0x7C)
        {
            var style = Marshal.PtrToStructure<StyleChange>(data);
            style.NewStyle |= 0x80000;
            Marshal.StructureToPtr(style, data, false);
        }
        else if (message == 0x7D) SetLayeredWindowAttributes(window, 0, 255, 2);
    }
    [StructLayout(LayoutKind.Sequential)] private struct StyleChange { public uint OldStyle, NewStyle; }
    public bool IsNativeWindowAlive(nint window) => IsWindow(window);
    public void EnsureDesktopMaterial(nint window)
    {
        if (!IsDesktopChild(window) || (ReadLong(window, -20) & 0x80000) != 0) return;
        WriteLong(window, -20, ReadLong(window, -20) | 0x80000);
        SetLayeredWindowAttributes(window, 0, 255, 2);
    }
    public bool IsDesktopChild(nint window) => worker != 0 && IsWindow(worker) && GetParent(window) == DesktopParent;
    public bool DesktopReceivesPointer(nint window)
    {
        GetWindowRect(window, out var rect);
        return WindowFromPoint(new NativePoint { X = rect.Left + 70, Y = rect.Top + 40 }) == window;
    }
    public string DesktopHitDiagnostic(nint window)
    {
        GetWindowRect(window, out var rect);
        var point = new NativePoint { X = rect.Left + 70, Y = rect.Top + 40 };
        var actual = WindowFromPoint(point);
        var actualName = new StringBuilder(256); GetClassName(actual, actualName, 256);
        GetWindowThreadProcessId(actual, out var actualProcess);
        var root = GetAncestor(window, 2); ScreenToClient(root, ref point);
        var hit = ChildWindowFromPointEx(root, point, 7);
        var name = new StringBuilder(256); GetClassName(hit, name, 256);
        var order = new List<string>();
        for (var child = GetWindow(root, 5); child != 0; child = GetWindow(child, 2))
        { GetWindowRect(child, out var r); order.Add($"{child}:visible={IsWindowVisible(child)},style={ReadLong(child,-16):X},ex={ReadLong(child,-20):X},bounds={r.Left},{r.Top},{r.Right},{r.Bottom}"); }
        return $"window={window}, style={ReadLong(window,-16):X}, exstyle={ReadLong(window,-20):X}, rect={rect.Left},{rect.Top},{rect.Right},{rect.Bottom}, actualHit={actual} ({actualName}, pid={actualProcess}), parent={GetParent(window)}, root={root}, hit={hit}, class={name}, order={string.Join(';',order)}";
    }
    public bool AttachToDesktop(nint window)
    {
        if (IsDesktopChild(window)) return true;
        if (!IsWindow(window)) return false;
        if (worker == 0 || !IsWindow(worker))
        {
            if (DateTime.UtcNow < nextHostAttempt) return false;
            nextHostAttempt = DateTime.UtcNow.AddSeconds(3);
            worker = FindDesktopWorker();
        }
        if (worker == 0) return false;
        GetWindowRect(window, out var rect);
        var style = ReadLong(window, -16);
        try
        {
            WriteLong(window, -16, (style & ~0x80000000L) | 0x40000000L);
            var extended = ReadLong(window, -20);
            WriteLong(window, -20, (extended | 0x80080) & ~0x40000L); // Layered tool window, never an Alt+Tab item.
            // WPF per-pixel windows already use UpdateLayeredWindow; LWA would disable that path.
            if ((extended & 0x80000) == 0) SetLayeredWindowAttributes(window, 0, 255, 2);
            Marshal.SetLastPInvokeError(0);
            if (SetParent(window, DesktopParent) == 0 && Marshal.GetLastPInvokeError() != 0) throw new Win32Exception();
            Place(window, rect.Left, rect.Top);
            desktopWindows.Add(window);
            return IsDesktopChild(window);
        }
        catch (Win32Exception)
        {
            SetParent(window, 0); WriteLong(window, -16, style);
            worker = 0; return false;
        }
    }
    private static nint FindDesktopWorker()
    {
        var progman = FindWindow("Progman", null);
        if (progman == 0) return 0;
        var existing = FindWindowEx(progman, 0, "WorkerW", null);
        if (existing != 0) return existing;
        // Explorer owns this undocumented desktop layer. Never hide/destroy its windows.
        SendMessageTimeout(progman, 0x052C, 0xD, 0, 2, 500, out _);
        SendMessageTimeout(progman, 0x052C, 0xD, 1, 2, 500, out _);
        var nested = FindWindowEx(progman, 0, "WorkerW", null);
        if (nested != 0) return nested;
        nint found = 0;
        EnumWindows((window, _) =>
        {
            if (FindWindowEx(window, 0, "SHELLDLL_DefView", null) != 0)
                found = FindWindowEx(0, window, "WorkerW", null);
            return found == 0;
        }, 0);
        return found;
    }
    public void SetWindowSwitching(bool switching)
    {
        if (IsSwitchingWindows == switching) return;
        IsSwitchingWindows = switching; DesktopCoverageChanged?.Invoke();
    }
    private static long ReadLong(nint h, int index) => (IntPtr.Size == 8 ? GetWindowLongPtr(h, index).ToInt64() : GetWindowLong(h, index)) & 0xFFFFFFFFL;
    private static void WriteLong(nint h, int index, long value)
    {
        Marshal.SetLastPInvokeError(0);
        var previous = IntPtr.Size == 8 ? SetWindowLongPtr(h, index, (nint)value) : (nint)SetWindowLong(h, index, unchecked((int)value));
        if (previous == 0 && Marshal.GetLastPInvokeError() != 0) throw new Win32Exception();
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetParent(nint window);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetParent(nint window, nint parent);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string name, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string name, string? title);
    [DllImport("user32.dll")] private static extern nint SendMessageTimeout(nint window, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nint result);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(nint window, ref NativePoint point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern nint ChildWindowFromPointEx(nint window, NativePoint point, uint flags);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(nint window, uint color, byte alpha, uint flags);
}
