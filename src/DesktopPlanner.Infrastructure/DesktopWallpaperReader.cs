using System.Runtime.InteropServices;
using DesktopPlanner.Domain;
namespace DesktopPlanner.Infrastructure;

public sealed record WallpaperInfo(string Path, ScreenRectangle Bounds, int Position, uint Background);
public static class DesktopWallpaperReader
{
    public static IReadOnlyList<WallpaperInfo> Read()
    {
        var result = new List<WallpaperInfo>();
        var type = Type.GetTypeFromCLSID(new Guid("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD"), true)!;
        var desktop = (IDesktopWallpaper)Activator.CreateInstance(type)!;
        try
        {
            desktop.GetMonitorDevicePathCount(out var count); desktop.GetPosition(out var position); desktop.GetBackgroundColor(out var color);
            for (uint i = 0; i < count; i++)
            {
                desktop.GetMonitorDevicePathAt(i, out var id); desktop.GetMonitorRECT(id, out var rect); desktop.GetWallpaper(id, out var path);
                result.Add(new(path, new(rect.Left, rect.Top, rect.Right, rect.Bottom), position, color));
            }
        }
        finally { Marshal.FinalReleaseComObject(desktop); }
        return result;
    }
    [StructLayout(LayoutKind.Sequential)] private struct WallpaperRect { public int Left, Top, Right, Bottom; }
    [ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDesktopWallpaper
    {
        void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.LPWStr)] string path);
        void GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.LPWStr)] out string path);
        void GetMonitorDevicePathAt(uint index, [MarshalAs(UnmanagedType.LPWStr)] out string id);
        void GetMonitorDevicePathCount(out uint count);
        void GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string id, out WallpaperRect rect);
        void SetBackgroundColor(uint color);
        void GetBackgroundColor(out uint color);
        void SetPosition(int position);
        void GetPosition(out int position);
    }
}
