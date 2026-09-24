using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopPlanner.Application;
using DesktopPlanner.Domain;
using DesktopPlanner.Infrastructure;
namespace DesktopPlanner.App;

public partial class WidgetWindow : Window
{
    public WidgetLayout Layout { get; }
    private readonly IPlannerStore store;
    private readonly WindowsOverlayService overlay;
    private readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool initialized, closing, desktopSuppressed, refreshingDesktop;
    private readonly GlassBackdropService glass;
    private nint Handle => new WindowInteropHelper(this).Handle;
    public event Action<Exception>? SaveFailed;
    public WidgetWindow(WidgetLayout layout, PlannerViewModel vm, IPlannerStore store, WindowsOverlayService overlay, GlassBackdropService glass)
    {
        InitializeComponent();
        this.glass = glass; Layout = layout; this.store = store; this.overlay = overlay; DataContext = vm;
        Title = Heading.Text = layout.WidgetType switch { WidgetType.Todo => "Задачи", WidgetType.Notes => "Заметки", WidgetType.Completed => "Готово", WidgetType.Week => "Неделя", _ => "События" };
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("WidgetContent.xaml", UriKind.Relative) });
        Body.Content = vm; Body.ContentTemplate = (DataTemplate)FindResource(layout.WidgetType.ToString());
        if (layout.WidgetType is WidgetType.Week or WidgetType.Inbox) Footer.Visibility = Visibility.Collapsed;
        Width = layout.Width; Height = layout.Height; Opacity = layout.Opacity;
        ApplyScale();
        SourceInitialized += (_, _) => { overlay.Place(Handle, Layout.X, Layout.Y); initialized = true; RefreshGlass(); };
        LocationChanged += (_, _) => { ScheduleSave(); RefreshGlass(); if (initialized) RefreshDesktopVisibility(overlay.GetDesktopObstructions()); };
        SizeChanged += (_, _) => { ScheduleSave(); RefreshGlass(); };
        saveTimer.Tick += async (_, _) => { saveTimer.Stop(); try { await FlushAsync(); } catch (Exception ex) { SaveFailed?.Invoke(ex); } };
        Closing += (_, e) => { if (!closing) { e.Cancel = true; SetVisible(false); } };
        PreviewKeyDown += async (_, e) =>
        {
            if (e.Key != Key.Z || Keyboard.Modifiers != ModifierKeys.Control || Keyboard.FocusedElement is TextBoxBase) return;
            for (var current = Keyboard.FocusedElement as DependencyObject; current is not null; current = VisualTreeHelper.GetParent(current))
                if (current is EventEditor) return;
            e.Handled = true; await vm.Calendar.UndoCommand.ExecuteAsync(null);
        };
    }
    public void SetVisible(bool visible)
    {
        Layout.IsVisible = visible;
        RefreshDesktopVisibility(overlay.GetDesktopObstructions());
        ScheduleSave();
    }
    public void RefreshDesktopVisibility(IReadOnlyList<ScreenRectangle> obstructions)
    {
        if (refreshingDesktop || closing) return;
        refreshingDesktop = true;
        try
        {
        // WPF Window rendering is not reliable after native reparenting into Explorer.
        // Keep the normal WPF composition path until a dedicated desktop host replaces it.
        if (WindowState == WindowState.Minimized)
        {
            var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            var stored = new ScreenRectangle(Layout.X, Layout.Y, Layout.X + Layout.Width * dpi, Layout.Y + Layout.Height * dpi);
            desktopSuppressed = obstructions.Any(bounds => bounds.Intersects(stored));
        }
        else desktopSuppressed = obstructions.Any(bounds => overlay.IntersectsWindow(Handle, bounds));
        desktopSuppressed |= overlay.IsSwitchingWindows;
        if (Layout.IsVisible && !desktopSuppressed) { if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; if (!IsVisible) Show(); }
        else if (IsVisible) Hide();
        }
        finally { refreshingDesktop = false; }
    }
    public void RefreshGlass()
    {
        if (GlassRoot is null) return;
        GlassRoot.Clip = new RectangleGeometry(new Rect(0, 0, Math.Max(0, GlassRoot.ActualWidth), Math.Max(0, GlassRoot.ActualHeight)), 28, 28);
        if (!initialized || Handle == 0 || WindowState == WindowState.Minimized) return;
        var position = overlay.GetPosition(Handle); var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var centerX = position.X + Width * dpi / 2; var centerY = position.Y + Height * dpi / 2;
        var backdrop = glass.Backdrops.FirstOrDefault(b => centerX >= b.Bounds.Left && centerX < b.Bounds.Right && centerY >= b.Bounds.Top && centerY < b.Bounds.Bottom);
        if (backdrop is null) return;
        Wallpaper.Source = backdrop.Image; Wallpaper.Width = (backdrop.Bounds.Right - backdrop.Bounds.Left) / dpi;
        Wallpaper.Height = (backdrop.Bounds.Bottom - backdrop.Bounds.Top) / dpi;
        Canvas.SetLeft(Wallpaper, (backdrop.Bounds.Left - position.X) / dpi - 8);
        Canvas.SetTop(Wallpaper, (backdrop.Bounds.Top - position.Y) / dpi - 8);
    }
    private void GlassPointerMove(object sender, MouseEventArgs e)
    {
        var point = e.GetPosition(GlassRoot);
        Specular.Center = Specular.GradientOrigin = new Point(point.X / Math.Max(1, GlassRoot.ActualWidth), point.Y / Math.Max(1, GlassRoot.ActualHeight));
    }    public void ApplyPreset(WidgetLayout preset)
    {
        if (Layout.IsPositionLocked) return;
        Layout.X = preset.X; Layout.Y = preset.Y; Layout.Width = preset.Width; Layout.Height = preset.Height;
        Layout.Scale = preset.Scale; Layout.Opacity = preset.Opacity; Layout.MonitorId = preset.MonitorId;
        Width = preset.Width; Height = preset.Height; Opacity = preset.Opacity; ApplyScale();
        overlay.Place(Handle, preset.X, preset.Y); ScheduleSave();
    }
    public void SetInteractionLock(bool locked)
    { if (Handle != 0) { if (locked) Keyboard.ClearFocus(); overlay.SetInteractionLock(Handle, locked); } }
    public void SetPositionLocked(bool locked)
    {
        Layout.IsPositionLocked = locked;
        ApplyScale(); ScheduleSave();
    }
    public void Recover()
    {
        LayoutRecovery.Recover(Layout, overlay.GetMonitors());
        Width = Layout.Width; Height = Layout.Height;
        if (Handle != 0) overlay.Place(Handle, Layout.X, Layout.Y);
        ScheduleSave();
    }
    private void ScheduleSave() { if (!initialized || closing || WindowState == WindowState.Minimized) return; saveTimer.Stop(); saveTimer.Start(); }
    public async Task FlushAsync()
    {
        saveTimer.Stop();
        if (Handle != 0 && initialized && overlay.IsNativeWindowAlive(Handle) && WindowState != WindowState.Minimized)
        {
            var position = overlay.GetPosition(Handle);
            Layout.X = position.X; Layout.Y = position.Y; Layout.MonitorId = position.MonitorId;
            Layout.Width = ActualWidth > 0 ? ActualWidth : Width; Layout.Height = ActualHeight > 0 ? ActualHeight : Height;
        }
        await store.SaveLayoutAsync(Layout);
    }
    public void CloseForExit() { saveTimer.Stop(); closing = true; Close(); }
    private void MoveWindow(object sender, MouseButtonEventArgs e)
    {
        if (Layout.IsPositionLocked || e.ChangedButton != MouseButton.Left) return;
        if (e.OriginalSource is DependencyObject source)
        {
            for (var current = source; current is not null && current != Header; current = VisualTreeHelper.GetParent(current))
                if (current is Button) return;
        }
        DragMove();
    }
    private void ResizeWidget(object sender, DragDeltaEventArgs e)
    {
        if (Layout.IsPositionLocked) return;
        Width = Math.Max(MinWidth, ActualWidth + e.HorizontalChange);
        Height = Math.Max(MinHeight, ActualHeight + e.VerticalChange);
    }
    private void ApplyScale()
    {
        Body.LayoutTransform = new ScaleTransform(Layout.Scale, Layout.Scale);
        ResizeThumb.IsEnabled = !Layout.IsPositionLocked;
    }
    private void HideWidget(object sender, RoutedEventArgs e) => SetVisible(false);
    private void OpenSettings(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var locked = new MenuItem { Header = "Закрепить положение", IsCheckable = true, IsChecked = Layout.IsPositionLocked };
        locked.Click += (_, _) => SetPositionLocked(locked.IsChecked);
        menu.Items.Add(locked);
        var scale = new MenuItem { Header = "Масштаб", IsEnabled = !Layout.IsPositionLocked };
        foreach (var value in new[] { .75, 1, 1.25, 1.5, 1.75 })
        {
            var item = new MenuItem { Header = $"{value:P0}", IsCheckable = true, IsChecked = Layout.Scale == value };
            item.Click += (_, _) => { Layout.Scale = value; ApplyScale(); ScheduleSave(); }; scale.Items.Add(item);
        }
        menu.Items.Add(scale);
        var opacity = new MenuItem { Header = "Непрозрачность" };
        foreach (var value in new[] { .4, .6, .8, .94, 1 })
        {
            var item = new MenuItem { Header = $"{value:P0}" };
            item.Click += (_, _) => { Layout.Opacity = value; Opacity = value; ScheduleSave(); }; opacity.Items.Add(item);
        }
        menu.Items.Add(opacity); menu.PlacementTarget = (Button)sender; menu.IsOpen = true;
    }
}








