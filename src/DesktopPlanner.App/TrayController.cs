using Forms = System.Windows.Forms;
using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using Media = System.Windows.Media;
namespace DesktopPlanner.App;

internal sealed class TrayController : IDisposable
{
    private readonly Forms.NotifyIcon icon;
    private readonly Icon calendarIcon = CreateIcon();
    private readonly ContextMenu menu = new() { Placement = PlacementMode.MousePoint };
    private readonly Separator widgetMenuMarker = new();
    private readonly List<MenuItem> widgetItems = [];
    private MenuItem? confirmationItem;
    private bool deleteInProgress;
    private readonly IReadOnlyList<WidgetWindow> widgets;
    private readonly Func<WidgetWindow, Task> deleteTracker;
    public TrayController(IReadOnlyList<WidgetWindow> widgets, Action toggleLock, Func<bool> isLocked, Action restore, Func<Task> arrange, Func<WidgetWindow, Task> deleteTracker, Func<Task> reset, Action openLogs, Func<Task> exit)
    {
        this.widgets = widgets;
        this.deleteTracker = deleteTracker;
        menu.Items.Add(new MenuItem { Header = "DesktopPlanner", IsEnabled = false });
        menu.Items.Add(new Separator());
        menu.Items.Add(widgetMenuMarker); RefreshWidgets();
        menu.Closed += (_, _) => HideDeleteConfirmation();
        menu.Opened += (_, _) =>
        {
            foreach (var item in widgetItems)
            {
                var widget = (WidgetWindow)item.Tag;
                if (item.Header is Grid row)
                {
                    ((TextBlock)row.Children[0]).Text = widget.Title;
                    System.Windows.Automation.AutomationProperties.SetName((Button)row.Children[1], $"Удалить {widget.Title}");
                }
                else item.Header = widget.Title;
                item.IsChecked = widget.Layout.IsVisible;
            }
        };
        var locked = new MenuItem { Header = "Пропускать ввод · Ctrl+Shift+Space", IsCheckable = true };
        locked.Click += (_, _) => toggleLock(); menu.Opened += (_, _) => locked.IsChecked = isLocked(); menu.Items.Add(locked);
        var pinned = new MenuItem { Header = "Закрепить все виджеты", IsCheckable = true };
        pinned.Click += async (_, _) =>
        {
            pinned.IsEnabled = false;
            try
            {
                var value = !widgets.All(w => w.Layout.IsPositionLocked);
                foreach (var widget in widgets) widget.SetPositionLocked(value);
                // Persist both the lock and the current geometry immediately, including hidden widgets.
                foreach (var widget in widgets) await widget.FlushAsync();
            }
            catch (Exception ex) { Notify("Не удалось сохранить закрепление виджетов: " + ex.Message); }
            finally { pinned.IsChecked = widgets.All(w => w.Layout.IsPositionLocked); pinned.IsEnabled = true; }
        };
        menu.Opened += (_, _) => pinned.IsChecked = widgets.All(w => w.Layout.IsPositionLocked);
        menu.Items.Add(pinned);
        AddAction("Вернуть виджеты на экраны", (_, _) => restore());
        var arrangeItem = AddAction("Сбросить расположение виджетов", async (_, _) => await arrange());
        menu.Opened += (_, _) => arrangeItem.IsEnabled = !widgets.Any(w => w.Layout.IsPositionLocked);
        var startup = new MenuItem { Header = "Запускать вместе с Windows", IsCheckable = true };
        startup.IsEnabled = Environment.GetEnvironmentVariable("DESKTOPPLANNER_DATA_DIR") is null;
        startup.Click += (_, _) =>
        {
            try { StartupRegistration.SetEnabled(!StartupRegistration.IsEnabled()); startup.IsChecked = StartupRegistration.IsEnabled(); }
            catch (Exception ex) { Notify("Не удалось изменить автозапуск: " + ex.Message); }
        };
        menu.Opened += (_, _) =>
        {
            try { startup.IsChecked = StartupRegistration.IsEnabled(); }
            catch (Exception ex) { Serilog.Log.Warning(ex, "Cannot read startup registration"); startup.IsEnabled = false; }
        };
        menu.Items.Add(startup);
        menu.Items.Add(new Separator());
        AddAction("Открыть папку логов", (_, _) => openLogs());
        AddAction("Сбросить все данные…", async (_, _) =>
        {
            menu.IsOpen = false;
            // Let the popup release activation/capture before entering a modal loop.
            await menu.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            await reset();
        });
        AddAction("Сохранить и выйти", async (_, _) => await exit());
        icon = new Forms.NotifyIcon { Text = "DesktopPlanner — виджеты рабочего стола", Icon = calendarIcon, Visible = true };
        menu.Opened += (_, _) => { if (PresentationSource.FromVisual(menu) is HwndSource source) SetForegroundWindow(source.Handle); };
        icon.MouseClick += (_, e) => { if (e.Button is Forms.MouseButtons.Left or Forms.MouseButtons.Right) { menu.IsOpen = false; menu.IsOpen = true; } };
    }
    public void RefreshWidgets()
    {
        HideDeleteConfirmation();
        foreach (var item in widgetItems) menu.Items.Remove(item);
        widgetItems.Clear();
        var index = menu.Items.IndexOf(widgetMenuMarker);
        foreach (var widget in widgets)
        {
            var item = new MenuItem { Header = widget.Title, IsCheckable = true, IsChecked = widget.Layout.IsVisible, StaysOpenOnClick = true, Tag = widget };
            item.Click += (_, _) =>
            {
                widget.SetVisible(!widget.Layout.IsVisible);
                item.IsChecked = widget.Layout.IsVisible;
            };
            if (widget.Layout.WidgetType == DesktopPlanner.Domain.WidgetType.MonthTracker && widget.Layout.TrackerId != DesktopPlanner.Domain.WidgetLayout.DefaultTrackerId)
            {
                var row = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new TextBlock { Text = widget.Title, VerticalAlignment = VerticalAlignment.Center });
                var delete = new Button { Style = (Style)System.Windows.Application.Current.FindResource("TrayDeleteButton"), ToolTip = "Удалить трекер" };
                System.Windows.Automation.AutomationProperties.SetName(delete, $"Удалить {widget.Title}");
                Grid.SetColumn(delete, 1);
                delete.Click += (_, e) =>
                {
                    e.Handled = true;
                    menu.Dispatcher.BeginInvoke(() => ShowDeleteConfirmation(widget), System.Windows.Threading.DispatcherPriority.ContextIdle);
                };
                row.Children.Add(delete);
                item.Header = row;
            }
            menu.Items.Insert(index++, item); widgetItems.Add(item);
        }
    }
    private void ShowDeleteConfirmation(WidgetWindow widget)
    {
        if (deleteInProgress || !widgets.Contains(widget)) return;
        HideDeleteConfirmation();
        foreach (var item in menu.Items.OfType<UIElement>()) item.Visibility = Visibility.Collapsed;

        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = "Удалить трекер?", FontWeight = FontWeights.SemiBold, FontSize = 15, Foreground = Media.Brushes.White });
        content.Children.Add(new TextBlock { Text = $"«{widget.Title}» и все его отметки будут удалены.", TextWrapping = TextWrapping.Wrap, Foreground = Media.Brushes.White, Margin = new Thickness(0, 8, 0, 12) });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Отмена", MinWidth = 76 };
        cancel.Click += (_, e) => { e.Handled = true; HideDeleteConfirmation(); };
        var confirm = new Button { Content = "Удалить", MinWidth = 76, Background = new Media.SolidColorBrush(Media.Color.FromRgb(151, 59, 72)) };
        confirm.Click += async (_, e) =>
        {
            e.Handled = true;
            if (deleteInProgress) return;
            deleteInProgress = true;
            confirm.IsEnabled = false;
            cancel.IsEnabled = false;
            menu.IsOpen = false;
            try
            {
                await menu.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                await deleteTracker(widget);
            }
            finally { deleteInProgress = false; }
        };
        actions.Children.Add(cancel); actions.Children.Add(confirm); content.Children.Add(actions);
        var card = new Border
        {
            Background = new Media.SolidColorBrush(Media.Color.FromArgb(238, 38, 57, 78)),
            BorderBrush = new Media.SolidColorBrush(Media.Color.FromArgb(120, 164, 207, 244)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(14),
            MinWidth = 250, MaxWidth = 320, Child = content
        };
        confirmationItem = new MenuItem { Header = card, Style = (Style)System.Windows.Application.Current.FindResource("TrayConfirmationItem") };
        menu.Items.Add(confirmationItem);
        menu.IsOpen = true;
    }
    private void HideDeleteConfirmation()
    {
        if (confirmationItem is null) return;
        menu.Items.Remove(confirmationItem);
        confirmationItem = null;
        foreach (var item in menu.Items.OfType<UIElement>()) item.Visibility = Visibility.Visible;
    }
    private MenuItem AddAction(string title, RoutedEventHandler action)
    {
        var item = new MenuItem { Header = title }; item.Click += action; menu.Items.Add(item); return item;
    }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint window);
    private static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var background = new SolidBrush(Color.FromArgb(55, 135, 224));
            using var foreground = new Pen(Color.White, 2);
            graphics.FillRectangle(background, 3, 5, 26, 24);
            graphics.DrawLine(foreground, 4, 12, 28, 12);
            graphics.DrawLine(foreground, 10, 2, 10, 8); graphics.DrawLine(foreground, 22, 2, 22, 8);
            using var fill = new SolidBrush(Color.White);
            graphics.FillEllipse(fill, 8, 17, 4, 4); graphics.FillEllipse(fill, 15, 17, 4, 4); graphics.FillEllipse(fill, 22, 17, 4, 4);
        }
        using var png = new System.IO.MemoryStream(); bitmap.Save(png, System.Drawing.Imaging.ImageFormat.Png);
        using var stream = new System.IO.MemoryStream(); using var writer = new System.IO.BinaryWriter(stream);
        writer.Write((short)0); writer.Write((short)1); writer.Write((short)1);
        writer.Write((byte)32); writer.Write((byte)32); writer.Write((byte)0); writer.Write((byte)0);
        writer.Write((short)1); writer.Write((short)32); writer.Write((int)png.Length); writer.Write(22); writer.Write(png.ToArray());
        stream.Position = 0; using var source = new Icon(stream); return (Icon)source.Clone();
    }
    public void Notify(string text) { icon.BalloonTipTitle = "DesktopPlanner"; icon.BalloonTipText = text; icon.ShowBalloonTip(5000); }
    public void Dispose() { icon.Visible = false; icon.Dispose(); calendarIcon.Dispose(); menu.IsOpen = false; }
}

