using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DesktopPlanner.Domain;
using DesktopPlanner.Application;
namespace DesktopPlanner.App;
public partial class TodoView : UserControl
{
    private Point origin;
    private TaskItem? dragged;
    public TodoView() => InitializeComponent();
    private void StopDrag(object sender, MouseButtonEventArgs e) => dragged = null;
    private void StartDrag(object sender, MouseButtonEventArgs e)
    {
        origin = e.GetPosition(this);
        dragged = null;
        var source = e.OriginalSource as DependencyObject;
        while (source is not null && source is not ListBoxItem)
        {
            if (source is System.Windows.Controls.Primitives.ButtonBase or TextBox) return;
            source = VisualTreeHelper.GetParent(source);
        }
        dragged = (source as ListBoxItem)?.DataContext as TaskItem;
    }
    private void DragTask(object sender, MouseEventArgs e)
    {
        var point = e.GetPosition(this);
        if (e.LeftButton != MouseButtonState.Pressed || dragged is null ||
            (Math.Abs(point.X - origin.X) < SystemParameters.MinimumHorizontalDragDistance &&
             Math.Abs(point.Y - origin.Y) < SystemParameters.MinimumVerticalDragDistance)) return;
        var task = dragged; dragged = null;
        var data = new DataObject(typeof(TaskItem), task);
        data.SetData(typeof(CalendarDrag), new CalendarDrag(CalendarSource.Task, task.Id));
        DragDrop.DoDragDrop(this, data, DragDropEffects.Move);
    }
    private async void DropTask(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(TaskItem)) is TaskItem task &&
            FindItem(e.OriginalSource as DependencyObject)?.DataContext is TaskItem target && DataContext is PlannerViewModel vm)
        { await vm.ReorderAsync(task.Id, target.Id); e.Handled = true; }
    }
    private static ListBoxItem? FindItem(DependencyObject? source)
    {
        while (source is not null && source is not ListBoxItem) source = VisualTreeHelper.GetParent(source);
        return source as ListBoxItem;
    }
    private void BeginRename(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || sender is not TextBlock title || title.DataContext is not TaskItem) return;
        StartRename(title);
        e.Handled = true;
    }
    private static void StartRename(TextBlock title)
    {
        if (VisualTreeHelper.GetParent(title) is not Grid panel || panel.Children.OfType<TextBox>().FirstOrDefault() is not { } editor) return;
        editor.Tag = editor.Text;
        title.Visibility = Visibility.Collapsed; editor.Visibility = Visibility.Visible;
        editor.Focus(); editor.SelectAll();
    }
    private async void RenameKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox editor) return;
        if (e.Key == Key.Enter) { await CommitRenameAsync(editor); e.Handled = true; }
        else if (e.Key == Key.Escape) { CancelRename(editor); e.Handled = true; }
    }
    private async void FinishRename(object sender, KeyboardFocusChangedEventArgs e)
    { if (sender is TextBox editor && editor.Visibility == Visibility.Visible) await CommitRenameAsync(editor); }
    private async Task CommitRenameAsync(TextBox editor)
    {
        if (editor.Visibility != Visibility.Visible) return;
        var title = editor.Text.Trim();
        if (editor.DataContext is TaskItem task && title.Length > 0 && title != (editor.Tag as string) && DataContext is PlannerViewModel vm)
            await vm.RenameAsync(task, title);
        CloseRename(editor);
    }
    private static void CancelRename(TextBox editor)
    { editor.Text = editor.Tag as string ?? editor.Text; CloseRename(editor); }
    private static void CloseRename(TextBox editor)
    {
        editor.Visibility = Visibility.Collapsed;
        if (VisualTreeHelper.GetParent(editor) is Grid panel && panel.Children.OfType<TextBlock>().FirstOrDefault() is { } title) title.Visibility = Visibility.Visible;
    }
    private void EditFromMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || item.Parent is not ContextMenu menu || menu.PlacementTarget is not Border target ||
            FindChild<TextBlock>(target) is not { } title) return;
        menu.IsOpen = false;
        StartRename(title);
    }
    private async void DeleteFromMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || item.Parent is not ContextMenu menu || menu.PlacementTarget is not Border target ||
            target.DataContext is not TaskItem task || DataContext is not PlannerViewModel vm) return;
        menu.IsOpen = false;
        await vm.DeleteCommand.ExecuteAsync(task);
    }
    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T result) return result;
            if (FindChild<T>(child) is { } nested) return nested;
        }
        return null;
    }
}


