using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using System.Windows.Input;
using System.Windows.Media;
using DesktopPlanner.Application;
using DesktopPlanner.Domain;
using DesktopPlanner.Infrastructure;

namespace DesktopPlanner.App;

public partial class ICloudSettingsWindow : Window
{
    private readonly CalendarSyncService sync;
    private readonly CancellationTokenSource lifetime = new();
    public ICloudSettingsWindow(CalendarSyncService sync, GlassBackdropService glass)
    {
        this.sync = sync; InitializeComponent();
        void RefreshGlass()
        {
            SettingsGlass.Clip = new RectangleGeometry(new Rect(0, 0, SettingsGlass.ActualWidth, SettingsGlass.ActualHeight), 28, 28);
            if (!IsLoaded) return;
            var origin = SettingsGlass.PointToScreen(new Point());
            var dpi = VisualTreeHelper.GetDpi(this);
            var backdrop = glass.Backdrops.FirstOrDefault(b => origin.X >= b.Bounds.Left && origin.X < b.Bounds.Right && origin.Y >= b.Bounds.Top && origin.Y < b.Bounds.Bottom)
                ?? glass.Backdrops.FirstOrDefault();
            if (backdrop is null) return;
            SettingsWallpaper.Source = backdrop.Image;
            SettingsWallpaper.Width = (backdrop.Bounds.Right - backdrop.Bounds.Left) / dpi.DpiScaleX;
            SettingsWallpaper.Height = (backdrop.Bounds.Bottom - backdrop.Bounds.Top) / dpi.DpiScaleY;
            Canvas.SetLeft(SettingsWallpaper, (backdrop.Bounds.Left - origin.X) / dpi.DpiScaleX);
            Canvas.SetTop(SettingsWallpaper, (backdrop.Bounds.Top - origin.Y) / dpi.DpiScaleY);
        }
        Loaded += (_, _) => RefreshGlass();
        SettingsGlass.SizeChanged += (_, _) => RefreshGlass(); LocationChanged += (_, _) => RefreshGlass();
        KeyDown += (_, e) => { if (e.Key == Key.Escape && !Calendars.IsDropDownOpen) { Close(); e.Handled = true; } };
        Loaded += async (_, _) => await RunAsync(LoadConnectionAsync);
        sync.StatusChanged += RefreshStatus;
        Closed += (_, _) => { sync.StatusChanged -= RefreshStatus; lifetime.Cancel(); PasswordInput.Clear(); };
    }
    private void CloseSettings(object sender, RoutedEventArgs e) => Close();
    private void MoveSettings(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        for (var current = e.OriginalSource as DependencyObject; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is System.Windows.Controls.Primitives.ButtonBase) return;
        DragMove();
    }
    private void RefreshStatus() => Dispatcher.BeginInvoke(() => { if (!lifetime.IsCancellationRequested) StatusLabel.Text = sync.Status; });
    private async Task LoadConnectionAsync()
    {
        var settings = await sync.GetSettingsAsync();
        if (settings is not null)
        {
            AccountInput.Text = settings.UserName;
            ConnectionLabel.Text = $"Подключён: {settings.CalendarName}" + (settings.LastSyncAt is { } time ? $"\nПоследний обмен: {time.ToLocalTime():dd.MM HH:mm}" : "");
        }
        else ConnectionLabel.Text = "iCloud пока не подключён";
        DisconnectButton.IsEnabled = settings is not null; StatusLabel.Text = sync.Status;
    }
    private void CredentialsChanged(object sender, RoutedEventArgs e)
    {
        if (Calendars is null || ConnectButton is null) return;
        Calendars.ItemsSource = null; ConnectButton.IsEnabled = false;
    }
    private void CalendarSelected(object sender, SelectionChangedEventArgs e)
    { if (ConnectButton is not null) ConnectButton.IsEnabled = Calendars.SelectedItem is RemoteCalendar; }
    private async void Discover(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var user = AccountInput.Text.Trim(); var password = PasswordInput.Password.Trim();
        if (user.Length == 0 || password.Length == 0) throw new InvalidOperationException("Введите Apple Account и пароль приложения.");
        StatusLabel.Text = "Ищем доступные календари…";
        using var provider = new ICloudCalendarProvider(user, password);
        var calendars = await provider.DiscoverAsync(new CalendarAccount(), lifetime.Token);
        Calendars.ItemsSource = calendars; Calendars.SelectedIndex = calendars.Count > 0 ? 0 : -1;
        StatusLabel.Text = calendars.Count == 0 ? "Не найдено календарей с правом записи. Проверьте Календарь на iCloud.com." : "Выберите календарь и нажмите «Подключить».";
    });
    private async void Connect(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (Calendars.SelectedItem is not RemoteCalendar calendar) return;
        await sync.ConnectAsync(AccountInput.Text.Trim(), PasswordInput.Password.Trim(), calendar, lifetime.Token);
        PasswordInput.Clear(); await LoadConnectionAsync(); await sync.SyncAsync(lifetime.Token);
        StatusLabel.Text = sync.Status;
    });
    private async void SyncNow(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    { await sync.SyncAsync(lifetime.Token); await LoadConnectionAsync(); });
    private async void Disconnect(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    { await sync.DisconnectAsync(lifetime.Token); PasswordInput.Clear(); await LoadConnectionAsync(); });
    private async Task RunAsync(Func<Task> action)
    {
        ConnectionForm.IsEnabled = false; ConnectionActions.IsEnabled = false;
        try { await action(); }
        catch (OperationCanceledException) { if (!lifetime.IsCancellationRequested) StatusLabel.Text = "Сервер не ответил. Попробуйте ещё раз."; }
        catch (System.Net.Http.HttpRequestException) { StatusLabel.Text = "Не удалось связаться с iCloud. Проверьте подключение к интернету."; }
        catch (Exception ex) { StatusLabel.Text = ex is InvalidOperationException or ArgumentException ? ex.Message : "Не удалось подключить iCloud. Проверьте данные и попробуйте снова."; }
        finally { ConnectionForm.IsEnabled = true; ConnectionActions.IsEnabled = true; }
    }
    private void OpenHelp(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try { Process.Start(new ProcessStartInfo("https://support.apple.com/ru-ru/102654") { UseShellExecute = true }); }
        catch (Exception) { StatusLabel.Text = "Откройте account.apple.com → Вход и безопасность → Пароли приложений."; }
    }
}
