using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DesktopPlanner.Infrastructure;

public sealed partial class WindowsOverlayService
{
    private nint keyboardHook;
    private KeyboardCallback? keyboardCallback;
    private void StartSwitcherTracking()
    {
        keyboardCallback = (code, message, data) =>
        {
            if (code >= 0)
            {
                var key = Marshal.PtrToStructure<KeyboardData>(data);
                var up = (key.Flags & 0x80) != 0;
                // Observe only the switcher chord; never store text or suppress keyboard input.
                if (!up && key.Key == 9 && (key.Flags & 0x20) != 0) SetWindowSwitching(true);
                if (up && key.Key is 0x12 or 0xA4 or 0xA5) SetWindowSwitching(false);
                if (!up && key.Key == 0x1B) SetWindowSwitching(false);
            }
            return CallNextHookEx(keyboardHook, code, message, data);
        };
        keyboardHook = SetWindowsHookEx(13, keyboardCallback, GetModuleHandle(null), 0);
        if (keyboardHook == 0) throw new Win32Exception();
    }
    private void StopSwitcherTracking()
    { if (keyboardHook != 0) UnhookWindowsHookEx(keyboardHook); keyboardHook = 0; keyboardCallback = null; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardData { public uint Key, Scan, Flags, Time; public nuint Extra; }
    private delegate nint KeyboardCallback(int code, nint message, nint data);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int type, KeyboardCallback callback, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? module);
}
