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
    private readonly DispatcherTimer noteTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly DispatcherTimer desktopTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private PlannerViewModel planner = null!;
    private WindowsOverlayService overlay = null!;
    private TrayController? tray;


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
            Log.Logger = new LoggerConfiguration().WriteTo.File(Path.Combine(directory, "logs", "planner-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7).CreateLogger();
            var collection = new ServiceCollection();
            collection.AddSingleton(_ => new SqlitePlannerStore(Path.Combine(directory, "planner.db")));
            collection.AddSingleton<IPlannerStore>(s => s.GetRequiredService<SqlitePlannerStore>());
            collection.AddSingleton<CalendarService>(); collection.AddSingleton<CalendarViewModel>();
            collection.AddSingleton<PlannerService>(); collection.AddSingleton<PlannerViewModel>(); collection.AddSingleton<WindowsOverlayService>();
            services = collection.BuildServiceProvider();
            var store = services.GetRequiredService<IPlannerStore>();
            await store.InitializeAsync();
            planner = services.GetRequiredService<PlannerViewModel>();
            overlay = services.GetRequiredService<WindowsOverlayService>();
            await planner.LoadAsync();
            var monitors = overlay.GetMonitors();
            var primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.First();
            await store.ApplyLayoutPresetAsync(ReferenceLayout.Create(primary), ReferenceLayout.Version);
            var layouts = await store.GetLayoutsAsync();
            foreach (var type in Enum.GetValues<WidgetType>())
            {
                var layout = layouts.SingleOrDefault(l => l.WidgetType == type) ?? CreateDefaultLayout(type, monitors);
                try { LayoutRecovery.Recover(layout, monitors); }
                catch (ArgumentOutOfRangeException ex)
                { Log.Warning(ex, "Reset invalid layout {Type}", type); layout = new WidgetLayout { WidgetType = type }; LayoutRecovery.Recover(layout, monitors); }
                var widget = new WidgetWindow(layout, planner, store, overlay);
                widget.SaveFailed += ShowSaveError;
                widgets.Add(widget);
                // Create the HWND even when hidden, so hotkey and restore operations are consistent.
                new WindowInteropHelper(widget).EnsureHandle();
                widget.RefreshDesktopVisibility(overlay.GetDesktopObstructions());
            }
            CreateTray();
            planner.Calendar.ShowRequested += () =>
            {
                var calendarWidget = widgets.FirstOrDefault(w => w.Layout.WidgetType == WidgetType.Week);
                calendarWidget?.SetVisible(true);
            };
            overlay.DesktopCoverageChanged += QueueDesktopRefresh;
            overlay.StartDesktopTracking(); QueueDesktopRefresh();
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
                    widget.Opacity = 0; widget.Show(); widget.UpdateLayout();
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    if (widget.Layout.WidgetType == WidgetType.Week) await SmokeDiagnostics.VerifySmoothScrollAsync(widget);
                    widget.Hide(); widget.Opacity = 1;
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
                        var fixtureBounds = overlay.GetDesktopObstructions(process);
                        if ((phase < 2) != fixtureBounds.Any()) throw new InvalidOperationException($"Fixture phase {phase}: unexpected desktop coverage");
                        var obstructions = overlay.GetDesktopObstructions();
                        foreach (var widget in widgets)
                        {
                            var handle = new WindowInteropHelper(widget).Handle;
                            var expectedVisible = widget.Layout.IsVisible && !overlay.IsSwitchingWindows && !obstructions.Any(b => overlay.IntersectsWindow(handle, b));
                            if (widget.IsVisible != expectedVisible || overlay.IsTopmost(handle)) throw new InvalidOperationException($"Desktop policy failed for {widget.Title} in phase {phase}");
                        }
                        if (Windows.Count != widgets.Count || tray is null) throw new InvalidOperationException("Unexpected settings window or missing tray");
                    });
                    if (widgets.Any(w => overlay.IsTopmost(new WindowInteropHelper(w).Handle))) throw new InvalidOperationException("Widget became topmost");
                    if (hidden.IsVisible || hidden.Layout.IsVisible) throw new InvalidOperationException("Fullscreen restored an intentionally hidden widget");
                    hidden.SetVisible(true);
                    var restored = widgets.Single(w => w.Layout.WidgetType == WidgetType.Todo);
                    restored.RefreshDesktopVisibility(overlay.GetDesktopObstructions());
                    overlay.SetWindowSwitching(true);
                    Log.Information("Desktop hit: {Hit}", overlay.DesktopHitDiagnostic(new WindowInteropHelper(restored).Handle));
                    foreach (var widget in widgets) widget.RefreshDesktopVisibility([]);
                    if (widgets.Any(w => w.IsVisible)) throw new InvalidOperationException("Widgets remained visible during window switching");
                    overlay.SetWindowSwitching(false);
                    foreach (var widget in widgets) widget.RefreshDesktopVisibility([]);
                    if (widgets.Any(w => w.IsVisible != w.Layout.IsVisible)) throw new InvalidOperationException("Window switching lost desired visibility");
                    Log.Information("Native desktop probe passed: desktop visibility, fullscreen/maximized/minimized fixture, switch suppression and tray only");
                    if (e.Args.Contains("--desktop-input-smoke"))
                    {
                        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
                        try
                        {
                            shell.MinimizeAll(); await Task.Delay(900);
                            foreach (var widget in widgets) widget.RefreshDesktopVisibility([]);
                            overlay.Place(new WindowInteropHelper(restored).Handle, primary.X, primary.Y);
                            restored.Opacity = 1; restored.InvalidateVisual(); restored.UpdateLayout();
                            await Task.Delay(300);
                            Log.Information("Native widget state: visible={Visible}, opacity={Opacity}, state={State}, enabled={Enabled}, size={Width}x{Height}", restored.IsVisible, restored.Opacity, restored.WindowState, restored.IsEnabled, restored.ActualWidth, restored.ActualHeight);
                            Log.Information("Uncovered desktop hit: {Hit}", overlay.DesktopHitDiagnostic(new WindowInteropHelper(restored).Handle));
                            if (!overlay.DesktopReceivesPointer(new WindowInteropHelper(restored).Handle)) throw new InvalidOperationException("Desktop widget is not interactive");
                            var position = overlay.GetPosition(new WindowInteropHelper(restored).Handle);
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
                noteTimer.Stop();
                await planner.Calendar.WhenIdleAsync();
                await planner.ResetDataAsync();
                foreach (var widget in widgets) ClearTextUndo(widget);
                if (planner.Todo.Count != 0 || planner.Completed.Count != 0 || planner.NoteText != ""
                    || planner.Calendar.Events.Count != 0 || planner.Calendar.Inbox.Count != 0)
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
                    foreach (var widget in widgets) widget.ApplyPreset(presets.Single(l => l.WidgetType == widget.Layout.WidgetType));
                    foreach (var widget in widgets) await widget.FlushAsync(); QueueDesktopRefresh();
                }
                catch (Exception ex) { ShowSaveError(ex); }
            }, ResetDataAsync, ExitAsync);
        if (!overlay.RegisterShortcut(source.Handle)) tray.Notify("Ctrl+Shift+Space занято. Переключайте блокировку через трей.");
    }    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    { handled = overlay.ProcessMessage(message, wParam); return 0; }
    private bool desktopRefreshPending;
    private void RecoverDesktopHosts()
    {
        if (exiting || services is null) return;
        try
        {
            for (var i = 0; i < widgets.Count; i++)
            {
                var widget = widgets[i];
                if (overlay.IsNativeWindowAlive(new WindowInteropHelper(widget).Handle)) continue;
                widget.CloseForExit();
                var replacement = new WidgetWindow(widget.Layout, planner, services.GetRequiredService<IPlannerStore>(), overlay);
                replacement.SaveFailed += ShowSaveError; widgets[i] = replacement;
                new WindowInteropHelper(replacement).EnsureHandle(); replacement.SetInteractionLock(interactionLocked);
                Log.Information("Recreated desktop widget {Type} after losing native host", widget.Layout.WidgetType);
            }
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
                var obstructions = overlay.GetDesktopObstructions();
                foreach (var widget in widgets) widget.RefreshDesktopVisibility(obstructions);
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
    private bool resetPending;
    private async Task ResetDataAsync()
    {
        if (exiting || exitPending || resetPending) return;
        resetPending = true;
        var resumeNoteTimer = noteTimer.IsEnabled;
        try
        {
            if (MessageBox.Show("Удалить все задачи, включая выполненные, события календаря, входящие события и заметки?\n\nОтменить сброс нельзя. Расположение и настройки виджетов сохранятся.",
                "Сбросить все данные", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            noteTimer.Stop();
            foreach (var widget in widgets) widget.IsEnabled = false;
            var commands = new[] { planner.AddCommand.ExecutionTask, planner.CompleteCommand.ExecutionTask,
                planner.RestoreCommand.ExecutionTask, planner.DeleteCommand.ExecutionTask,
                planner.ShowInCalendarCommand.ExecutionTask, planner.ReorderTask }.OfType<Task>();
            await Task.WhenAll(commands);
            await planner.Calendar.WhenIdleAsync();
            await planner.ResetDataAsync();
            foreach (var widget in widgets) ClearTextUndo(widget);
            resumeNoteTimer = false;
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













