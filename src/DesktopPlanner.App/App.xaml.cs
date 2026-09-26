using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using DesktopPlanner.Application;
using DesktopPlanner.Domain;
using DesktopPlanner.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
namespace DesktopPlanner.App;

public partial class App : System.Windows.Application
{
    private ServiceProvider? services;
    private readonly List<WidgetWindow> widgets = [];
    private readonly Dictionary<string, MonthTrackerViewModel> trackerModels = [];
    private readonly DispatcherTimer noteTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly DispatcherTimer desktopTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private PlannerViewModel planner = null!;
    private WindowsOverlayService overlay = null!;
    private IPlannerStore store = null!;
    private TrayController? tray;
    private string logsDirectory = "";


    private Mutex? instance;
    private bool interactionLocked, exiting;
    private HwndSource? source;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--fullscreen-fixture")) { SmokeDiagnostics.RunFullscreenFixture(this); return; }
        var isolatedSmoke = e.Args.Contains("--smoke-test") && Environment.GetEnvironmentVariable("DESKTOPPLANNER_DATA_DIR") is not null;
        instance = new Mutex(true, isolatedSmoke ? $"Local\\DesktopPlanner.Smoke.{Environment.ProcessId}" : "Local\\DesktopPlanner.Foundation", out var created);
        if (!created) { Shutdown(); return; }
        try
        {
            var directory = Environment.GetEnvironmentVariable("DESKTOPPLANNER_DATA_DIR")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopPlanner");
            Directory.CreateDirectory(directory);
            logsDirectory = Path.Combine(directory, "logs");
            Directory.CreateDirectory(logsDirectory);
            Log.Logger = new LoggerConfiguration().WriteTo.File(Path.Combine(logsDirectory, "planner-.txt"),
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}").CreateLogger();
            DispatcherUnhandledException += (_, args) => { Log.Fatal(args.Exception, "Unhandled UI exception"); Log.CloseAndFlush(); };
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            { Log.Fatal(args.ExceptionObject as Exception, "Unhandled process exception"); Log.CloseAndFlush(); };
            var collection = new ServiceCollection();
            collection.AddSingleton(_ => new SqlitePlannerStore(Path.Combine(directory, "planner.db")));
            collection.AddSingleton<IPlannerStore>(s => s.GetRequiredService<SqlitePlannerStore>());
            collection.AddSingleton<CalendarService>(); collection.AddSingleton<CalendarViewModel>(); collection.AddSingleton<MonthTrackerViewModel>();
            collection.AddSingleton<PlannerService>(); collection.AddSingleton<PlannerViewModel>(); collection.AddSingleton<WindowsOverlayService>();
            services = collection.BuildServiceProvider();
            store = services.GetRequiredService<IPlannerStore>();
            await store.InitializeAsync();
            planner = services.GetRequiredService<PlannerViewModel>();
            overlay = services.GetRequiredService<WindowsOverlayService>();
            await planner.LoadAsync();
            var monitors = overlay.GetMonitors();
            var primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.First();
            await store.ApplyLayoutPresetAsync(ReferenceLayout.Create(primary), ReferenceLayout.Version);
            var layouts = await store.GetLayoutsAsync();
            foreach (var type in Enum.GetValues<WidgetType>().Where(type => type != WidgetType.MonthTracker))
            {
                var layout = layouts.SingleOrDefault(l => l.WidgetType == type) ?? CreateDefaultLayout(type, monitors);
                try { LayoutRecovery.Recover(layout, monitors); }
                catch (ArgumentOutOfRangeException ex)
                { Log.Warning(ex, "Reset invalid layout {Type}", type); layout = new WidgetLayout { WidgetType = type }; LayoutRecovery.Recover(layout, monitors); }
                CreateWidget(layout, null);
            }
            foreach (var layout in layouts.Where(layout => layout.WidgetType == WidgetType.MonthTracker).OrderBy(layout => layout.Id))
            {
                var tracker = layout.TrackerId == WidgetLayout.DefaultTrackerId
                    ? planner.MonthTracker : new MonthTrackerViewModel(store, layout.TrackerId, layout.TrackerColorHex);
                tracker.SetColor(layout.TrackerColorHex);
                if (layout.TrackerId != WidgetLayout.DefaultTrackerId) await tracker.LoadAsync();
                trackerModels[layout.TrackerId] = tracker;
                CreateWidget(layout, tracker);
            }
            CreateTray();
#if DEBUG
            _ = Dispatcher.BeginInvoke(() =>
            {
                var handle = widgets[0].Handle;
                Log.Information("Desktop hierarchy:{NewLine}{Hierarchy}", Environment.NewLine,
                    overlay.DesktopHierarchyDiagnostic(handle));
                Log.Information("Desktop hit: {Hit}", overlay.DesktopHitDiagnostic(handle));
                Log.Information("Desktop render: {Render}", widgets[0].RenderDiagnostic());
                Log.Information("Desktop content hit: {Hit}", overlay.DesktopHitDiagnostic(widgets[0].ContentHandle));
            }, DispatcherPriority.ApplicationIdle);
#endif
            planner.Calendar.ShowRequested += () =>
            {
                var calendarWidget = widgets.FirstOrDefault(w => w.Layout.WidgetType == WidgetType.Week);
                calendarWidget?.SetVisible(true);
            };
            QueueDesktopRefresh();
            desktopTimer.Tick += (_, _) => RecoverDesktopHosts(); desktopTimer.Start();
            planner.NoteChanged += () => { noteTimer.Stop(); noteTimer.Start(); };
            noteTimer.Tick += async (_, _) => { noteTimer.Stop(); try { await planner.SaveNoteAsync(); } catch (Exception ex) { ShowSaveError(ex); } };
            overlay.InteractionToggleRequested += ToggleInteraction;
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
            Log.Information("DesktopPlanner started with {Count} widgets", widgets.Count);
            if (e.Args.Contains("--smoke-test"))
            {
                if (Environment.GetEnvironmentVariable("DESKTOPPLANNER_DATA_DIR") is null)
                    throw new InvalidOperationException("Smoke test requires an isolated DESKTOPPLANNER_DATA_DIR.");
                planner.NewTitle = "Спланировать неделю"; await planner.AddCommand.ExecuteAsync(null);
                planner.NewTitle = "Проверить сохранение виджетов"; await planner.AddCommand.ExecuteAsync(null);
                var completedTask = planner.Todo.Last();
                await planner.CompleteCommand.ExecuteAsync(completedTask);
                await planner.RestoreCommand.ExecuteAsync(planner.Completed.First(t => t.Id == completedTask.Id));
                await planner.CompleteCommand.ExecuteAsync(planner.Todo.First(t => t.Id == completedTask.Id));
                planner.NoteText = "На этой неделе\n\n• Выбрать главное\n• Оставить время для отдыха\n\nЗаметки сохраняются автоматически.";
                await planner.SaveNoteAsync();
                var calendar = planner.Calendar;
                await planner.MonthTracker.ToggleDayCommand.ExecuteAsync(DateTime.Today);
                if (!(await store.GetHabitDayMarksAsync(WidgetLayout.DefaultTrackerId, DateTime.Today, DateTime.Today.AddDays(1))).Any()) throw new InvalidOperationException("Month tracker mark was not saved");
                calendar.NewTitle = "Встреча с дизайнером"; calendar.NewDescription = "Обсудить макеты";
                await calendar.AddInboxCommand.ExecuteAsync(null);
                var inboxId = calendar.Inbox.Last().Id;
                await calendar.ScheduleAsync(new CalendarDrag(CalendarSource.Inbox, inboxId), calendar.WeekStart.AddHours(9));
                if (!calendar.LastOperationSucceeded || calendar.Inbox.Any(i => i.Id == inboxId)) throw new InvalidOperationException("Inbox conversion failed");
                var taskId = planner.Todo.First().Id;
                await calendar.ScheduleAsync(new CalendarDrag(CalendarSource.Task, taskId), calendar.WeekStart.AddHours(9.5));
                var taskEventId = planner.Todo.First(t => t.Id == taskId).CalendarEventId ?? throw new InvalidOperationException("Task was not linked");
                await calendar.ScheduleAsync(new CalendarDrag(CalendarSource.Event, taskEventId), calendar.WeekStart.AddHours(10));
                await calendar.ResizeAsync(taskEventId, calendar.WeekStart.AddHours(11.5));
                if (!calendar.LastOperationSucceeded || planner.Todo.First(t => t.Id == taskId).ScheduledEnd != calendar.WeekStart.AddHours(11.5)) throw new InvalidOperationException("Linked duration was not saved");
                await planner.CompleteCommand.ExecuteAsync(planner.Todo.First(t => t.Id == taskId));
                if (calendar.Events.Any(e => e.Id == taskEventId)) throw new InvalidOperationException("Completed task remained visible in calendar");
                await planner.RestoreCommand.ExecuteAsync(planner.Completed.First(t => t.Id == taskId));
                if (!calendar.Events.Any(e => e.Id == taskEventId)) throw new InvalidOperationException("Restored task lost its calendar event");
                calendar.NewTitle = "Позвонить Ивану"; await calendar.AddInboxCommand.ExecuteAsync(null);
                var currentWeek = calendar.WeekStart;
                await calendar.NextWeekCommand.ExecuteAsync(null); await calendar.PreviousWeekCommand.ExecuteAsync(null);
                if (calendar.WeekStart != currentWeek || !calendar.Events.Any(e => e.Id == taskEventId)) throw new InvalidOperationException("Week navigation failed");
                var weekWindow = widgets.Single(w => w.Layout.WidgetType == WidgetType.Week);
                // Keep the real preset dimensions for visual QA.
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                foreach (var widget in widgets)
                {
                    widget.Measure(new Size(widget.Width, widget.Height));
                    widget.Arrange(new Rect(0, 0, widget.Width, widget.Height));
                    widget.UpdateLayout();
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    if (widget.Layout.WidgetType == WidgetType.Week) await SmokeDiagnostics.VerifySmoothScrollAsync(widget);
                    widget.RefreshGlass();
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)widget.ActualWidth, (int)widget.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    var content = (FrameworkElement)widget.Content;
                    content.Measure(new Size(widget.Width, widget.Height)); content.Arrange(new Rect(0, 0, widget.Width, widget.Height)); content.UpdateLayout();
                    bitmap.Render(content);
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(directory, widget.Layout.WidgetType + ".png")); encoder.Save(file);
                }
                SmokeDiagnostics.SaveLayoutPreview(widgets, directory);
                if (e.Args.Contains("--fullscreen-smoke"))
                {
                    var hidden = widgets.Single(w => w.Layout.WidgetType == WidgetType.Notes);
                    hidden.SetVisible(false);
                    await SmokeDiagnostics.RunDesktopProbeAsync((process, phase) =>
                    {
                        foreach (var widget in widgets)
                        {
                            var handle = widget.Handle;
                            if (widget.IsVisible != widget.Layout.IsVisible || !overlay.IsDesktopOwned(handle) || overlay.IsTopmost(handle))
                                throw new InvalidOperationException($"Desktop hosting failed for {widget.Title} in phase {phase}: visible={widget.IsVisible}, desired={widget.Layout.IsVisible}, owned={overlay.IsDesktopOwned(handle)}, topmost={overlay.IsTopmost(handle)}, {overlay.DesktopHitDiagnostic(handle)}");
                        }
                        if (Windows.Count != 0 || tray is null) throw new InvalidOperationException("Unexpected settings window or missing tray");
                    });
                    if (widgets.Any(w => overlay.IsTopmost(w.Handle))) throw new InvalidOperationException("Widget became topmost");
                    if (hidden.IsVisible || hidden.Layout.IsVisible) throw new InvalidOperationException("Fullscreen restored an intentionally hidden widget");
                    hidden.SetVisible(true);
                    var restored = widgets.Single(w => w.Layout.WidgetType == WidgetType.Todo);
                    restored.RefreshDesktopVisibility();
                    Log.Information("Desktop hit: {Hit}", overlay.DesktopHitDiagnostic(restored.Handle));
                    Log.Information("Native desktop probe passed: hosted visibility, fullscreen/maximized/minimized fixture and tray only");
                    if (e.Args.Contains("--desktop-input-smoke"))
                    {
                        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
                        try
                        {
                            shell.MinimizeAll(); await Task.Delay(900);
                            foreach (var widget in widgets) widget.RefreshDesktopVisibility();
                            overlay.Place(restored.Handle, primary.X, primary.Y);
                            restored.Opacity = 1; restored.InvalidateVisual(); restored.UpdateLayout();
                            await Task.Delay(300);
                            Log.Information("Native widget state: visible={Visible}, opacity={Opacity}, enabled={Enabled}, size={Width}x{Height}", restored.IsVisible, restored.Opacity, restored.IsEnabled, restored.ActualWidth, restored.ActualHeight);
                            Log.Information("Uncovered desktop hit: {Hit}", overlay.DesktopHitDiagnostic(restored.Handle));
                            if (!overlay.DesktopReceivesPointer(restored.Handle)) throw new InvalidOperationException("Desktop widget is not interactive");
                            var position = overlay.GetPosition(restored.Handle);
                            using var capture = new System.Drawing.Bitmap((int)restored.Width, (int)restored.Height);
                            SmokeDiagnostics.CaptureDesktop(capture, (int)position.X, (int)position.Y);
                            capture.Save(Path.Combine(directory, "DesktopNative.png"));
                            await SmokeDiagnostics.VerifyAltTabAsync(overlay, widgets);
                        }
                        finally { shell.UndoMinimizeALL(); System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
                    }
                }
                ToggleInteraction(); ToggleInteraction();
                // Exercise reset only against the isolated smoke-test database.
                var taskCount = planner.Todo.Count + planner.Completed.Count;
                await SmokeDiagnostics.VerifyResetDialogAsync(ResetDataAsync, QueueDesktopRefresh, false);
                if (planner.Todo.Count + planner.Completed.Count != taskCount || (await store.GetTasksAsync()).Count != taskCount)
                    throw new InvalidOperationException("Cancelling reset changed tasks");
                await SmokeDiagnostics.VerifyResetDialogAsync(ResetDataAsync, QueueDesktopRefresh, true);
                if (planner.Todo.Count != 0 || planner.Completed.Count != 0 || planner.NoteText != ""
                    || planner.Calendar.Events.Count != 0 || planner.Calendar.Inbox.Count != 0
                    || (await store.GetHabitDayMarksAsync(WidgetLayout.DefaultTrackerId, DateTime.Today, DateTime.Today.AddDays(1))).Count != 0)
                    throw new InvalidOperationException("Reset did not clear every widget");
                await planner.SaveNoteAsync();
                if ((await store.GetTasksAsync()).Count != 0 || await store.GetNoteAsync() != "" || await store.UndoCalendarAsync())
                    throw new InvalidOperationException("Reset data was restored by a pending save or undo");
                await ExitAsync();
            }
        }
        catch (Exception ex) { Log.Fatal(ex, "Startup failed"); if (!e.Args.Contains("--smoke-test")) MessageBox.Show(ex.ToString(), "DesktopPlanner: ошибка запуска"); Shutdown(1); }
    }
    private static WidgetLayout CreateDefaultLayout(WidgetType type, IReadOnlyList<MonitorArea> monitors)
    {
        var monitor = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
        if (monitor is null) return new WidgetLayout { WidgetType = type };
        return ReferenceLayout.Create(monitor).Single(l => l.WidgetType == type);
    }
    private void CreateWidget(WidgetLayout layout, MonthTrackerViewModel? tracker)
    {
        var widget = new WidgetWindow(layout, planner, store, overlay, tracker);
        widget.SaveFailed += ShowSaveError;
        widget.AddTrackerRequested += AddTrackerAsync;
        widgets.Add(widget);
        tray?.RefreshWidgets();
        widget.RefreshDesktopVisibility();
    }
    private async void AddTrackerAsync()
    {
        if (widgets.Count(widget => widget.Layout.WidgetType == WidgetType.MonthTracker) >= 4)
        { tray?.Notify("Можно добавить не более четырёх трекеров."); return; }
        try
        {
            var monitors = overlay.GetMonitors();
            var primary = monitors.FirstOrDefault(monitor => monitor.IsPrimary) ?? monitors[0];
            var layout = CreateDefaultLayout(WidgetType.MonthTracker, monitors);
            var count = widgets.Count(widget => widget.Layout.WidgetType == WidgetType.MonthTracker);
            layout.TrackerId = Guid.NewGuid().ToString("N"); layout.TrackerTitle = $"Трекер {count + 1}";
            layout.TrackerColorHex = new[] { "#7EE2A8", "#FFD166", "#FF8FA3" }[count - 1];
            layout.X = Math.Min(layout.X + count * 32, primary.X + primary.Width - layout.Width);
            layout.Y = Math.Min(layout.Y + count * 32, primary.Y + primary.Height - layout.Height);
            await store.SaveLayoutAsync(layout);
            var tracker = new MonthTrackerViewModel(store, layout.TrackerId, layout.TrackerColorHex);
            await tracker.LoadAsync(); trackerModels.Add(layout.TrackerId, tracker);
            CreateWidget(layout, tracker); QueueDesktopRefresh();
        }
        catch (Exception ex) { ShowSaveError(ex); }
    }
    private void CreateTray()
    {
        source = new HwndSource(new HwndSourceParameters("DesktopPlanner messages") { ParentWindow = new nint(-3), WindowStyle = 0, Width = 0, Height = 0 });
        source.AddHook(WindowMessage);
        tray = new TrayController(widgets, ToggleInteraction, () => interactionLocked,
            () => { foreach (var widget in widgets) { widget.Recover(); widget.SetVisible(true); } QueueDesktopRefresh(); },
            async () =>
            {
                try
                {
                    var monitors = overlay.GetMonitors();
                    var presets = ReferenceLayout.Create(monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0]);
                    foreach (var widget in widgets.Where(widget => widget.Layout.WidgetType != WidgetType.MonthTracker || widget.Layout.TrackerId == WidgetLayout.DefaultTrackerId))
                        widget.ApplyPreset(presets.Single(layout => layout.WidgetType == widget.Layout.WidgetType));
                    foreach (var widget in widgets) await widget.FlushAsync(); QueueDesktopRefresh();
                }
                catch (Exception ex) { ShowSaveError(ex); }
            }, ResetDataAsync, OpenLogs, ExitAsync);
        if (!overlay.RegisterShortcut(source.Handle)) tray.Notify("Ctrl+Shift+Space занято. Переключайте блокировку через трей.");
    }    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    { handled = overlay.ProcessMessage(message, wParam); return 0; }
    private bool desktopRefreshPending;
    private void RecoverDesktopHosts()
    {
        if (exiting || services is null) return;
        try
        {
            foreach (var widget in widgets) widget.RefreshDesktopVisibility();
            QueueDesktopRefresh();
        }
        catch (Exception ex) { Log.Warning(ex, "Desktop host recovery will retry"); }
    }
    private void QueueDesktopRefresh()
    {
        if (exiting || desktopRefreshPending) return;
        desktopRefreshPending = true;
        Dispatcher.BeginInvoke(() =>
        {
            desktopRefreshPending = false;
            if (exiting) return;
            try
            {
                foreach (var widget in widgets) widget.RefreshDesktopVisibility();
            }
            catch (Exception ex) { ShowSaveError(ex); }
        }, DispatcherPriority.Normal);
    }
    private void ToggleInteraction() => SetInteraction(!interactionLocked);
    private void SetInteraction(bool locked)
    {
        interactionLocked = locked;
        foreach (var widget in widgets) widget.SetInteractionLock(locked);

    }
    private async void DisplaySettingsChanged(object? sender, EventArgs e)
    {
        try
        {
            await Dispatcher.InvokeAsync(() =>
            {
                foreach (var widget in widgets) { widget.Recover(); widget.RefreshGlass(); }
                QueueDesktopRefresh();
            });
        }
        catch (Exception ex) { ShowSaveError(ex); }
    }    private void ShowSaveError(Exception ex)
    { Log.Error(ex, "Save or window operation failed"); tray?.Notify("Ошибка: " + ex.Message + " · повторите сохранение перед выходом."); }
    private bool exitPending;
    private void OpenLogs()
    {
        try
        {
            Directory.CreateDirectory(logsDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(logsDirectory) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowSaveError(ex); }
    }
    private bool resetPending;
    private async Task ResetDataAsync()
    {
        if (exiting || exitPending || resetPending) return;
        resetPending = true;
        var resumeNoteTimer = noteTimer.IsEnabled;
        try
        {
            Log.Information("Data reset confirmation opened");
            if (new ResetConfirmationWindow().ShowDialog() != true)
            { Log.Information("Data reset cancelled"); return; }
            Log.Information("Data reset confirmed");
            noteTimer.Stop();
            foreach (var widget in widgets) widget.IsEnabled = false;
            var commands = new[] { planner.AddCommand.ExecutionTask, planner.CompleteCommand.ExecutionTask,
                planner.RestoreCommand.ExecutionTask, planner.DeleteCommand.ExecutionTask,
                planner.ShowInCalendarCommand.ExecutionTask, planner.ReorderTask }.OfType<Task>();
            await Task.WhenAll(commands);
            await planner.Calendar.WhenIdleAsync();
            await planner.ResetDataAsync();
            foreach (var tracker in trackerModels.Values.Where(tracker => tracker != planner.MonthTracker)) tracker.ClearAfterReset();
            foreach (var widget in widgets) ClearTextUndo(widget);
            resumeNoteTimer = false;
            Log.Information("All widget data and undo history cleared");
            tray?.Notify("Все данные удалены из виджетов.");
        }
        catch (Exception ex) { ShowSaveError(ex); }
        finally
        {
            resetPending = false;
            foreach (var widget in widgets) widget.IsEnabled = true;
            if (resumeNoteTimer) noteTimer.Start();
        }
    }
    private static void ClearTextUndo(DependencyObject element)
    {
        if (element is System.Windows.Controls.Primitives.TextBoxBase text && text.IsUndoEnabled)
        {
            text.SetCurrentValue(System.Windows.Controls.Primitives.TextBoxBase.IsUndoEnabledProperty, false);
            text.SetCurrentValue(System.Windows.Controls.Primitives.TextBoxBase.IsUndoEnabledProperty, true);
        }
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(element); i++)
            ClearTextUndo(System.Windows.Media.VisualTreeHelper.GetChild(element, i));
    }
    private async Task ExitAsync()
    {
        if (exiting || exitPending || resetPending) return;
        exitPending = true; noteTimer.Stop();
        foreach (var widget in widgets) widget.IsEnabled = false;
        try
        {
            // Finish user commands before disposing services or closing windows.
            var commands = new[] { planner.AddCommand.ExecutionTask, planner.CompleteCommand.ExecutionTask,
                planner.RestoreCommand.ExecutionTask, planner.DeleteCommand.ExecutionTask, planner.ReorderTask }.OfType<Task>();
            await Task.WhenAll(commands);
            await planner.Calendar.SaveViewportAsync();
            await planner.Calendar.WhenIdleAsync();
            await planner.SaveNoteAsync();
            foreach (var widget in widgets) await widget.FlushAsync();
            exiting = true;
            foreach (var widget in widgets) widget.CloseForExit();
            Shutdown();
        }
        catch (Exception ex) { ShowSaveError(ex); }
        finally { exitPending = false; if (!exiting) { foreach (var widget in widgets) widget.IsEnabled = true; } }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
        source?.RemoveHook(WindowMessage); source?.Dispose(); tray?.Dispose(); noteTimer.Stop(); desktopTimer.Stop();
        services?.Dispose(); instance?.Dispose(); Log.CloseAndFlush(); base.OnExit(e);
    }
}













