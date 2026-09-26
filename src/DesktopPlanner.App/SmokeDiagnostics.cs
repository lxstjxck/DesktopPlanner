using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopPlanner.Domain;
namespace DesktopPlanner.App;

internal static class SmokeDiagnostics
{
    public static async Task VerifyResetDialogAsync(Func<Task> reset, Action refreshDesktop, bool confirm)
    {
        var ticks = 0;
        Exception? failure = null;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        timer.Tick += (_, _) =>
        {
            var dialog = System.Windows.Application.Current.Windows.OfType<ResetConfirmationWindow>().SingleOrDefault();
            try
            {
                if (dialog is null || !dialog.IsVisible || !dialog.IsEnabled || dialog.Owner is not null)
                    throw new InvalidOperationException("Reset dialog disappeared or inherited a desktop owner");
                refreshDesktop();
                if (++ticks < 4) return;
                timer.Stop();
                if (!confirm) dialog.Close();
                else
                {
                    var panel = (System.Windows.Controls.StackPanel)dialog.Content;
                    var buttons = panel.Children.OfType<System.Windows.Controls.StackPanel>().Single();
                    buttons.Children.OfType<System.Windows.Controls.Button>().Single(b => !b.IsCancel)
                        .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                }
            }
            catch (Exception ex) { failure = ex; timer.Stop(); dialog?.Close(); }
        };
        timer.Start();
        try { await reset(); }
        finally { timer.Stop(); }
        if (failure is not null) throw failure;
        if (ticks != 4) throw new InvalidOperationException("Reset dialog closed before user action");
        Serilog.Log.Information("Reset dialog remained visible through desktop refresh; confirmed={Confirmed}", confirm);
    }
    public static void CaptureDesktop(System.Drawing.Bitmap bitmap, int x, int y)
    {
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        var destination = graphics.GetHdc(); var source = GetDC(0);
        try { if (!BitBlt(destination, 0, 0, bitmap.Width, bitmap.Height, source, x, y, 0x40CC0020)) throw new System.ComponentModel.Win32Exception(); }
        finally { ReleaseDC(0, source); graphics.ReleaseHdc(destination); }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll", SetLastError = true)] private static extern bool BitBlt(nint destination, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);
    public static async Task VerifyAltTabAsync(DesktopPlanner.Infrastructure.WindowsOverlayService overlay, IReadOnlyList<WidgetWindow> widgets)
    {
        var foreground = GetForegroundWindow();
        try
        {
            keybd_event(0x12, 0, 0, 0); keybd_event(9, 0, 0, 0); keybd_event(9, 0, 2, 0);
            await Task.Delay(450);
            if (widgets.Any(w => w.IsVisible != w.Layout.IsVisible ||
                !overlay.IsDesktopOwned(w.Handle) ||
                !overlay.IsToolWindow(w.Handle) ||
                overlay.IsTopmost(w.Handle)))
                throw new InvalidOperationException("Alt+Tab changed desktop widget visibility or topmost state");
        }
        finally { keybd_event(9, 0, 2, 0); keybd_event(0x12, 0, 2, 0); SetForegroundWindow(foreground); }
        await Task.Delay(350);
        if (widgets.Any(w => w.IsVisible != w.Layout.IsVisible)) throw new InvalidOperationException("Alt+Tab changed desired visibility");
        Serilog.Log.Information("Real Alt+Tab probe passed: desktop widgets retained visibility");
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, nuint extra);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    public static void RunFullscreenFixture(System.Windows.Application app)
    {
        var window = new Window { Title = "DesktopPlanner fullscreen test fixture", WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, Left = 0, Top = 0, Width = SystemParameters.PrimaryScreenWidth,
            Height = SystemParameters.PrimaryScreenHeight, Background = Brushes.SlateBlue, ShowActivated = true };
        var phase = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            if (phase++ == 0) { window.WindowStyle = WindowStyle.SingleBorderWindow; window.Width = 640; window.Height = 400; window.WindowState = WindowState.Maximized; }
            else if (phase == 2) window.WindowState = WindowState.Minimized;
            else { timer.Stop(); window.Close(); app.Shutdown(); }
        };
        window.Show(); window.Hide(); window.Show(); window.Activate();
        File.WriteAllText(Path.Combine(Environment.GetEnvironmentVariable("DESKTOPPLANNER_DATA_DIR")!, "fixture-state.txt"), $"visible={window.IsVisible}, active={window.IsActive}, bounds={window.Left},{window.Top},{window.ActualWidth},{window.ActualHeight}");
        timer.Start();
    }
    public static async Task RunDesktopProbeAsync(Action<int, int> verify)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!, "--fullscreen-fixture")
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        using var fixture = Process.Start(start) ?? throw new InvalidOperationException("Cannot start desktop fixture");
        await Task.Delay(1100); verify(fixture.Id, 0);
        await Task.Delay(2100); verify(fixture.Id, 1);
        await Task.Delay(2100); verify(fixture.Id, 2);
        await fixture.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }    public static async Task VerifySmoothScrollAsync(WidgetWindow window)
    {
        static T? Find<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent is T match) return match;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                if (Find<T>(VisualTreeHelper.GetChild(parent, i)) is { } child) return child;
            return null;
        }
        var view = Find<WeekView>(window) ?? throw new InvalidOperationException("Week view missing");
        var scroll = Find<System.Windows.Controls.ScrollViewer>(view) ?? throw new InvalidOperationException("Calendar scrolling missing");
        var start = scroll.VerticalOffset;
        scroll.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, -120)
            { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseWheelEvent });
        await Task.Delay(65); var middle = scroll.VerticalOffset;
        await Task.Delay(220); var finish = scroll.VerticalOffset;
        if (middle <= start || finish <= middle || finish > start + 79) throw new InvalidOperationException($"Smooth scroll failed: {start}, {middle}, {finish}");
        view.BeginAnimation(WeekView.SmoothOffsetProperty, null); scroll.ScrollToVerticalOffset(start);
        await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Serilog.Log.Information("Smooth scrolling probe passed: intermediate and final wheel positions verified");
        var calendar = (CalendarViewModel)view.DataContext;
        var week = calendar.WeekStart;
        var ids = calendar.Events.Select(item => item.Id).Order().ToArray();
        var next = (System.Windows.Controls.Button)view.FindName("NextButton");
        next.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        await Task.Delay(65);
        var shift = (TranslateTransform)view.FindName("PageShift");
        if (SystemParameters.ClientAreaAnimation && shift.X >= 0) throw new InvalidOperationException("Week slide did not start to the left");
        // A repeated request during the transition must not skip a second week.
        await view.NavigateWeekAsync(1);
        await Task.Delay(600);
        if (calendar.WeekStart != week.AddDays(7)) throw new InvalidOperationException("Week navigation skipped or failed");
        await view.NavigateWeekAsync(-1);
        await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        if (calendar.WeekStart != week || !ids.SequenceEqual(calendar.Events.Select(item => item.Id).Order()) || Math.Abs(scroll.VerticalOffset - start) > 1 || shift.X != 0)
            throw new InvalidOperationException($"Week slide failed: week={calendar.WeekStart}, expected={week}, events={ids.SequenceEqual(calendar.Events.Select(item => item.Id).Order())}, offset={scroll.VerticalOffset}, expectedOffset={start}, shift={shift.X}");
        Serilog.Log.Information("Week slide probe passed: direction, repeated click guard, round trip and hour position");
    }
    public static void SaveLayoutPreview(IReadOnlyList<WidgetWindow> widgets, string directory)
    {
        var minX = widgets.Min(w => w.Layout.X); var minY = widgets.Min(w => w.Layout.Y);
        var dpi = VisualTreeHelper.GetDpi(widgets[0]).DpiScaleX;
        var width = (int)Math.Ceiling(widgets.Max(w => (w.Layout.X - minX) / dpi + w.ActualWidth) + 48);
        var height = (int)Math.Ceiling(widgets.Max(w => (w.Layout.Y - minY) / dpi + w.ActualHeight) + 48);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            // Neutral synthetic backdrop for comparing the actual transparent window renders.
            drawing.DrawRectangle(new LinearGradientBrush(Color.FromRgb(54, 93, 146), Color.FromRgb(120, 159, 188), 35), null, new Rect(0, 0, width, height));
            foreach (var widget in widgets)
            {
                var bitmap = new BitmapImage(new Uri(Path.Combine(directory, widget.Layout.WidgetType + ".png")));
                drawing.DrawImage(bitmap, new Rect(24 + (widget.Layout.X - minX) / dpi, 24 + (widget.Layout.Y - minY) / dpi, widget.ActualWidth, widget.ActualHeight));
            }
        }
        var render = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); render.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(render));
        using var file = File.Create(Path.Combine(directory, "DesktopLayout.png")); encoder.Save(file);
    }
}




