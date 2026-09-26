using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace DesktopPlanner.Infrastructure;

public sealed partial class WindowsOverlayService
{
    private const string DesktopHostClass = "DesktopPlanner.NativeWidgetHost";
    private static readonly NativeWindowProcedure HostProcedure = DesktopHostWindowProcedure;
    private static bool hostClassRegistered;
    private nint desktopParent;
    private nint desktopIcons;
    private nint DesktopParent => desktopParent;
    private DateTime nextHostAttempt;
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
    public void PrepareDesktopWidget(nint window)
    {
        var extended = ReadLong(window, -20);
        WriteLong(window, -20, (extended | 0x80) & ~0x40008L); // Tool window, no app/taskbar or topmost style.
    }
    public void EnsureDesktopMaterial(nint window)
    {
        if (!IsDesktopChild(window) || (ReadLong(window, -20) & 0x80000) != 0) return;
        WriteLong(window, -20, ReadLong(window, -20) | 0x80000);
        SetLayeredWindowAttributes(window, 0, 255, 2);
    }
    public bool IsDesktopChild(nint window) => DesktopParent != 0 && IsWindow(DesktopParent) &&
        IsWindow(desktopIcons) && GetParent(desktopIcons) == DesktopParent && GetParent(window) == DesktopParent;
    public bool IsDesktopOwned(nint window) => DesktopParent != 0 && IsWindow(DesktopParent) &&
        IsWindow(desktopIcons) && GetParent(desktopIcons) == DesktopParent && GetWindow(window, 4) == DesktopParent;
    public void PlaceDesktopOwned(nint window, double x, double y)
    {
        if (!SetWindowPos(window, 0, (int)Math.Round(x), (int)Math.Round(y), 0, 0,
            0x0001 | 0x0004 | 0x0010)) throw new Win32Exception();
    }
    public void PositionDesktopOwned(nint window)
    {
        var aboveDesktop = GetWindow(DesktopParent, 3);
        if (aboveDesktop == 0 || !SetWindowPos(window, aboveDesktop, 0, 0, 0, 0,
            0x0001 | 0x0002 | 0x0010)) throw new Win32Exception();
    }
    public bool CanHostDesktopWidget()
    {
        if (TryFindDesktop(out var parent, out var icons))
        {
            desktopParent = parent; desktopIcons = icons; return true;
        }
        if (DateTime.UtcNow < nextHostAttempt) return false;
        nextHostAttempt = DateTime.UtcNow.AddSeconds(3);
        FindDesktopWorker();
        if (!TryFindDesktop(out parent, out icons)) return false;
        desktopParent = parent; desktopIcons = icons; return true;
    }
    public nint GetDesktopParent() => CanHostDesktopWidget() ? DesktopParent : 0;
    public nint CreateDesktopHost(int width, int height)
    {
        var parent = GetDesktopParent();
        if (parent == 0) return 0;
        RegisterDesktopHostClass();
        var window = CreateWindowEx(0x80, DesktopHostClass, null, 0x46000000,
            0, 0, width, height, parent, 0, GetModuleHandle(null), 0);
        if (window == 0) throw new Win32Exception();
        desktopWindows.Add(window);
        return window;
    }
    private static void RegisterDesktopHostClass()
    {
        if (hostClassRegistered) return;
        var registration = new NativeWindowClass
        {
            Size = (uint)Marshal.SizeOf<NativeWindowClass>(),
            Procedure = Marshal.GetFunctionPointerForDelegate(HostProcedure),
            Instance = GetModuleHandle(null),
            Name = DesktopHostClass
        };
        if (RegisterClassEx(ref registration) == 0) throw new Win32Exception();
        hostClassRegistered = true;
    }
    private static nint DesktopHostWindowProcedure(nint window, uint message, nint parameter, nint data)
    {
        if (message == 0x14) return 1; // WM_ERASEBKGND: leave child WPF surface unobscured.
        if (message == 0x0F) { ValidateRect(window, 0); return 0; } // WM_PAINT
        return DefWindowProc(window, message, parameter, data);
    }
    private delegate nint NativeWindowProcedure(nint window, uint message, nint parameter, nint data);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeWindowClass
    {
        public uint Size, Style;
        public nint Procedure;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Menu;
        [MarshalAs(UnmanagedType.LPWStr)] public string Name;
        public nint SmallIcon;
    }
    public void DestroyDesktopHost(nint window)
    {
        desktopWindows.Remove(window);
        roundedSizes.Remove(window);
        if (window != 0 && IsWindow(window)) DestroyWindow(window);
    }
    public void SetDesktopWidgetVisible(nint window, bool visible)
    {
        if (window != 0 && IsWindow(window)) ShowWindow(window, visible ? 4 : 0);
    }
    public void ResizeDesktopWidget(nint window, int width, int height)
    {
        if (window != 0 && IsWindow(window) && !SetWindowPos(window, 0, 0, 0, width, height, 0x0002 | 0x0004 | 0x0010))
            throw new Win32Exception();
    }
    public void RaiseDesktopWidget(nint window)
    {
        if (IsDesktopChild(window) && !SetWindowPos(window, 0, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010))
            throw new Win32Exception();
    }
    public bool DesktopReceivesPointer(nint window)
    {
        GetWindowRect(window, out var rect);
        return WindowFromPoint(new NativePoint { X = rect.Left + 70, Y = rect.Top + 40 }) == window;
    }
    public string DesktopHierarchyDiagnostic(nint widget)
    {
        var lines = new List<string>();
        void Describe(nint window, string role)
        {
            if (window == 0) return;
            var name = new StringBuilder(128); GetClassName(window, name, name.Capacity);
            GetWindowThreadProcessId(window, out var process);
            GetWindowRect(window, out var rect);
            lines.Add($"{role}: hwnd={window}, class={name}, pid={process}, parent={GetParent(window)}, owner={GetWindow(window, 4)}, " +
                $"style={ReadLong(window, -16):X}, ex={ReadLong(window, -20):X}, visible={IsWindowVisible(window)}, " +
                $"rect={rect.Left},{rect.Top},{rect.Right},{rect.Bottom}");
        }
        EnumWindows((window, _) =>
        {
            var name = new StringBuilder(32); GetClassName(window, name, name.Capacity);
            if (name.ToString() is not ("Progman" or "WorkerW")) return true;
            Describe(window, "desktop");
            var view = FindWindowEx(window, 0, "SHELLDLL_DefView", null);
            Describe(view, "icons");
            if (view != 0) Describe(FindWindowEx(view, 0, "SysListView32", null), "icon list");
            return true;
        }, 0);
        Describe(widget, "DesktopPlanner widget");
        if (desktopParent != 0 && IsWindow(desktopParent))
        {
            var index = 0;
            for (var child = GetWindow(desktopParent, 5); child != 0; child = GetWindow(child, 2))
                Describe(child, $"desktop child z={index++}");
        }
        return string.Join(Environment.NewLine, lines);
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
        if (IsDesktopChild(window) && TryFindDesktop(out var currentParent, out var currentIcons) &&
            currentParent == DesktopParent && currentIcons == desktopIcons) return true;
        if (!IsWindow(window)) return false;
        if (!TryFindDesktop(out var parent, out var icons)) return false;
        desktopParent = parent; desktopIcons = icons;
        GetWindowRect(window, out var rect);
        var style = ReadLong(window, -16);
        try
        {
            WriteLong(window, -16, (style & ~0x80000000L) | 0x40000000L);
            var extended = ReadLong(window, -20);
            WriteLong(window, -20, (extended | 0x80080) & ~0x40008L); // Layered tool window, never topmost or an Alt+Tab item.
            // WPF per-pixel windows already use UpdateLayeredWindow; LWA would disable that path.
            if ((extended & 0x80000) == 0) SetLayeredWindowAttributes(window, 0, 255, 2);
            Marshal.SetLastPInvokeError(0);
            if (SetParent(window, DesktopParent) == 0 && Marshal.GetLastPInvokeError() != 0) throw new Win32Exception();
            Place(window, rect.Left, rect.Top);
            RaiseDesktopWidget(window);
            desktopWindows.Add(window);
            return IsDesktopChild(window);
        }
        catch (Win32Exception)
        {
            SetParent(window, 0); WriteLong(window, -16, style);
            desktopParent = 0; desktopIcons = 0; return false;
        }
    }
    private static nint FindDesktopWorker()
    {
        var progman = FindWindow("Progman", null);
        if (progman == 0) return 0;
        // Explorer owns this undocumented desktop layer. Never hide/destroy its windows.
        SendMessageTimeout(progman, 0x052C, 0xD, 0, 2, 500, out _);
        SendMessageTimeout(progman, 0x052C, 0xD, 1, 2, 500, out _);
        return TryFindDesktop(out var parent, out _) ? parent : 0;
    }
    private static bool TryFindDesktop(out nint parent, out nint icons)
    {
        parent = 0; icons = 0;
        var progman = FindWindow("Progman", null);
        if (progman == 0) return false;
        GetWindowThreadProcessId(progman, out var explorerProcess);
        var direct = FindWindowEx(progman, 0, "SHELLDLL_DefView", null);
        if (direct != 0) { parent = progman; icons = direct; return true; }
        nint foundParent = 0, foundIcons = 0;
        EnumWindows((window, _) =>
        {
            var name = new StringBuilder(32);
            GetClassName(window, name, name.Capacity);
            if (name.ToString() != "WorkerW") return true;
            GetWindowThreadProcessId(window, out var process);
            if (process != explorerProcess) return true;
            var view = FindWindowEx(window, 0, "SHELLDLL_DefView", null);
            if (view == 0) return true;
            foundParent = window; foundIcons = view; return false;
        }, 0);
        parent = foundParent; icons = foundIcons;
        return parent != 0;
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
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref NativeWindowClass registration);
    [DllImport("user32.dll")] private static extern nint DefWindowProc(nint window, uint message, nint parameter, nint data);
    [DllImport("user32.dll")] private static extern bool ValidateRect(nint window, nint rectangle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(uint extended, string className, string? title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint data);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
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
