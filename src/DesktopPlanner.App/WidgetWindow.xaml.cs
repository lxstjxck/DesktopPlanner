using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using DesktopPlanner.Application;
using DesktopPlanner.Domain;
using DesktopPlanner.Infrastructure;
namespace DesktopPlanner.App;

public partial class WidgetWindow : UserControl
{
    public WidgetLayout Layout { get; }
    private readonly IPlannerStore store;
    private readonly WindowsOverlayService overlay;
    private readonly MonthTrackerViewModel? monthTracker;
    private readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool initialized, closing, refreshingDesktop, interactionLocked;
    private HwndSource? contentSource;
    private nint hostWindow;
    private bool desktopVisible, dragging;
    private System.Drawing.Point dragStart;
    private (double X, double Y) dragPosition;
    public nint Handle => hostWindow;
    public new bool IsVisible => desktopVisible;
    public string Title { get; private set; } = "";
    public event Action<Exception>? SaveFailed;
    public event Action? AddTrackerRequested;
    public WidgetWindow(WidgetLayout layout, PlannerViewModel vm, IPlannerStore store, WindowsOverlayService overlay, MonthTrackerViewModel? monthTracker = null)
    {
        InitializeComponent();
        Layout = layout; this.store = store; this.overlay = overlay; this.monthTracker = monthTracker; DataContext = vm;
        var title = layout.WidgetType switch { WidgetType.Todo => "Задачи", WidgetType.Notes => "Заметки", WidgetType.Completed => "Готово", WidgetType.Week => "Неделя", WidgetType.MonthTracker => string.IsNullOrWhiteSpace(layout.TrackerTitle) ? "Трекер" : layout.TrackerTitle, _ => "События" };
        Title = Heading.Text = title;
        if (layout.WidgetType == WidgetType.MonthTracker)
        {
            Layout.TrackerId = string.IsNullOrWhiteSpace(Layout.TrackerId) ? WidgetLayout.DefaultTrackerId : Layout.TrackerId;
            Layout.TrackerTitle = title;
            Layout.TrackerColorHex = string.IsNullOrWhiteSpace(Layout.TrackerColorHex) ? "#5CC8FF" : Layout.TrackerColorHex;
            monthTracker?.SetColor(Layout.TrackerColorHex);
            TrackerHeading.Text = title; TrackerHeading.Visibility = Visibility.Visible; Heading.Visibility = Visibility.Collapsed;
        }
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("WidgetContent.xaml", UriKind.Relative) });
        if (layout.WidgetType == WidgetType.MonthTracker && monthTracker is not null)
            Body.Content = new MonthTrackerView { DataContext = monthTracker };
        else { Body.Content = vm; Body.ContentTemplate = (DataTemplate)FindResource(layout.WidgetType.ToString()); }
        if (layout.WidgetType is WidgetType.Week or WidgetType.Inbox or WidgetType.MonthTracker) Footer.Visibility = Visibility.Collapsed;
        Width = layout.Width; Height = layout.Height; ApplyBackgroundOpacity();
        ApplyScale();
        SizeChanged += (_, _) =>
        {
            if (Handle != 0)
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                var width = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX);
                var height = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY);
                overlay.ResizeDesktopWidget(Handle, width, height);
            }
            ScheduleSave(); RefreshGlass();
        };
        saveTimer.Tick += async (_, _) => { saveTimer.Stop(); try { await FlushAsync(); } catch (Exception ex) { SaveFailed?.Invoke(ex); } };
        PreviewMouseMove += DragWidget;
        PreviewMouseLeftButtonUp += (_, _) => { if (dragging) { dragging = false; ReleaseMouseCapture(); ScheduleSave(); } };
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
        RefreshDesktopVisibility();
        ScheduleSave();
    }
    public void RefreshDesktopVisibility()
    {
        if (refreshingDesktop || closing) return;
        refreshingDesktop = true;
        try
        {
            if (contentSource is not null && (!overlay.IsNativeWindowAlive(Handle) || !overlay.IsDesktopOwned(Handle)))
                ReleaseHost();
            if (!Layout.IsVisible || !overlay.CanHostDesktopWidget())
            {
                Hide();
                return;
            }
            if (contentSource is null) CreateHost();
            Show();
        }
        finally { refreshingDesktop = false; }
    }
    private void CreateHost()
    {
        var parent = overlay.GetDesktopParent();
        if (parent == 0) return;
        var monitor = overlay.GetMonitors().FirstOrDefault(area => area.Id == Layout.MonitorId);
        var scale = monitor?.DpiScale ?? 1;
        var physicalWidth = (int)Math.Ceiling(Width * scale);
        var physicalHeight = (int)Math.Ceiling(Height * scale);
        var parameters = new HwndSourceParameters("DesktopPlannerWidget", physicalWidth, physicalHeight)
        {
            ParentWindow = parent,
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP; Explorer owns its Z-order group.
            ExtendedWindowStyle = 0x00000080, // WS_EX_TOOLWINDOW.
            UsesPerPixelOpacity = true
        };
        parameters.SetPosition((int)Math.Round(Layout.X), (int)Math.Round(Layout.Y));
        contentSource = new HwndSource(parameters);
        contentSource.CompositionTarget.BackgroundColor = Colors.Transparent;
        contentSource.RootVisual = this;
        hostWindow = contentSource.Handle;
        overlay.PlaceDesktopOwned(Handle, Layout.X, Layout.Y);
        overlay.PositionDesktopOwned(Handle);
        if (interactionLocked) SetInteractionLock(true);
        initialized = true;
        RefreshGlass();
    }
    private void ReleaseHost()
    {
        desktopVisible = false;
        if (contentSource is not null)
        {
            contentSource.RootVisual = null;
            contentSource.Dispose();
            contentSource = null;
        }
        hostWindow = 0;
    }
    public void Show()
    {
        if (Handle == 0 || !overlay.IsDesktopOwned(Handle)) return;
        overlay.SetDesktopWidgetVisible(Handle, true);
        desktopVisible = true;
    }
    public void Hide()
    {
        overlay.SetDesktopWidgetVisible(Handle, false);
        desktopVisible = false;
    }
    public string RenderDiagnostic() => $"nativeHost={Handle}, content={contentSource?.Handle}, rootVisible={base.IsVisible}, " +
        $"contentVisible={contentSource?.RootVisual is UIElement element && element.IsVisible}, size={ActualWidth}x{ActualHeight}, source={PresentationSource.FromVisual(this) is not null}";
    public nint ContentHandle => contentSource?.Handle ?? 0;
    public void RefreshGlass()
    {
        if (GlassRoot is null) return;
        GlassRoot.Clip = new RectangleGeometry(new Rect(0, 0, Math.Max(0, GlassRoot.ActualWidth), Math.Max(0, GlassRoot.ActualHeight)), 28, 28);
    }
    private void ApplyBackgroundOpacity()
    {
        BackgroundMaterial.Opacity = Layout.Opacity;
        BackgroundShadow.Opacity = Layout.Opacity;
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
        Width = preset.Width; Height = preset.Height; ApplyBackgroundOpacity(); ApplyScale();
        if (Handle != 0) overlay.Place(Handle, preset.X, preset.Y);
        ScheduleSave();
    }
    public void SetInteractionLock(bool locked)
    {
        interactionLocked = locked;
        if (Handle == 0) return;
        if (locked) Keyboard.ClearFocus();
        overlay.SetInteractionLock(Handle, locked);
    }
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
    private void ScheduleSave() { if (!initialized || closing) return; saveTimer.Stop(); saveTimer.Start(); }
    public async Task FlushAsync()
    {
        saveTimer.Stop();
        if (Handle != 0 && initialized && overlay.IsNativeWindowAlive(Handle))
        {
            var position = overlay.GetPosition(Handle);
            Layout.X = position.X; Layout.Y = position.Y; Layout.MonitorId = position.MonitorId;
            Layout.Width = ActualWidth > 0 ? ActualWidth : Width; Layout.Height = ActualHeight > 0 ? ActualHeight : Height;
        }
        await store.SaveLayoutAsync(Layout);
    }
    public void CloseForExit() { saveTimer.Stop(); closing = true; ReleaseHost(); }
    private void MoveWindow(object sender, MouseButtonEventArgs e)
    {
        if (Layout.IsPositionLocked || e.ChangedButton != MouseButton.Left) return;
        if (e.OriginalSource is DependencyObject source)
        {
            for (var current = source; current is not null && current != Header; current = VisualTreeHelper.GetParent(current))
                if (current is Button or TextBoxBase) return;
        }
        if (Handle == 0) return;
        dragging = true;
        dragStart = Forms.Cursor.Position;
        var position = overlay.GetPosition(Handle);
        dragPosition = (position.X, position.Y);
        CaptureMouse();
        e.Handled = true;
    }
    private void DragWidget(object sender, MouseEventArgs e)
    {
        if (!dragging || Handle == 0 || e.LeftButton != MouseButtonState.Pressed) return;
        var pointer = Forms.Cursor.Position;
        overlay.Place(Handle, dragPosition.X + pointer.X - dragStart.X, dragPosition.Y + pointer.Y - dragStart.Y);
        ScheduleSave();
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
    private void TrackerNameChanged(object sender, TextChangedEventArgs e)
    {
        if (Layout.WidgetType != WidgetType.MonthTracker) return;
        var title = TrackerHeading.Text.Trim();
        if (string.IsNullOrWhiteSpace(title)) return;
        Layout.TrackerTitle = title; Title = title; ScheduleSave();
    }
    private void TrackerNameLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (Layout.WidgetType != WidgetType.MonthTracker || !string.IsNullOrWhiteSpace(TrackerHeading.Text)) return;
        TrackerHeading.Text = Layout.TrackerTitle;
    }
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
        var opacity = new MenuItem { Header = "Непрозрачность фона" };
        foreach (var value in new[] { .4, .6, .8, .94, 1 })
        {
            var item = new MenuItem { Header = $"{value:P0}" };
            item.Click += (_, _) => { Layout.Opacity = value; ApplyBackgroundOpacity(); ScheduleSave(); }; opacity.Items.Add(item);
        }
        menu.Items.Add(opacity);
        if (Layout.WidgetType == WidgetType.MonthTracker)
        {
            var color = new MenuItem { Header = "Цвет трекера…" };
            color.Click += (_, _) => ChooseTrackerColor();
            menu.Items.Add(color);
            var addTracker = new MenuItem { Header = "Добавить трекер" };
            addTracker.Click += (_, _) => AddTrackerRequested?.Invoke();
            menu.Items.Add(addTracker);
        }
        menu.PlacementTarget = (Button)sender; menu.IsOpen = true;
    }
    private void ChooseTrackerColor()
    {
        if (monthTracker is null) return;
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(Layout.TrackerColorHex);
            using var picker = new Forms.ColorDialog { FullOpen = true, Color = System.Drawing.Color.FromArgb(color.R, color.G, color.B) };
            if (picker.ShowDialog(new ColorPickerOwner(Handle)) != Forms.DialogResult.OK) return;
            Layout.TrackerColorHex = $"#{picker.Color.R:X2}{picker.Color.G:X2}{picker.Color.B:X2}";
            monthTracker.SetColor(Layout.TrackerColorHex); ScheduleSave();
        }
        catch (Exception ex) { SaveFailed?.Invoke(ex); }
    }
    private sealed record ColorPickerOwner(nint Handle) : Forms.IWin32Window;
}








