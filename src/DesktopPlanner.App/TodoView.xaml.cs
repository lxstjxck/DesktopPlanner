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
    private void StartDrag(object sender, MouseButtonEventArgs e)
    {
        origin = e.GetPosition(this);
        dragged = null;
        var source = e.OriginalSource as DependencyObject;
        while (source is not null && source is not ListBoxItem)
        {
            if (source is System.Windows.Controls.Primitives.ButtonBase) return;
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
}


