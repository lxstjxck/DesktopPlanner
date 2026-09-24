using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DesktopPlanner.Application;
using DesktopPlanner.Domain;
namespace DesktopPlanner.App;
public partial class InboxView : UserControl
{
    private Point origin;
    private UnscheduledEvent? dragged;
    public InboxView()
    {
        InitializeComponent();
        // Selection/scrolling inside ListBox can handle mouse moves before bubbling.
        InboxItems.AddHandler(Mouse.PreviewMouseMoveEvent, new MouseEventHandler(DragEvent), true);
        Unloaded += (_, _) => dragged = null;
    }
    private void EndDrag(object sender, MouseButtonEventArgs e) => dragged = null;
    private void StartDrag(object sender, MouseButtonEventArgs e)
    {
        origin = e.GetPosition(InboxItems); dragged = null;
        var source = e.OriginalSource as DependencyObject;
        while (source is not null && source is not ListBoxItem)
        {
            if (source is ButtonBase) return;
            source = source is FrameworkContentElement content ? content.Parent : VisualTreeHelper.GetParent(source);
        }
        dragged = (source as ListBoxItem)?.DataContext as UnscheduledEvent;
    }
    private void DragEvent(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) { dragged = null; return; }
        var point = e.GetPosition(InboxItems);
        if (dragged is null ||
            (Math.Abs(point.X - origin.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - origin.Y) < SystemParameters.MinimumVerticalDragDistance)) return;
        var item = dragged; dragged = null;
        var data = new DataObject(typeof(UnscheduledEvent), item);
        data.SetData(typeof(CalendarDrag), new CalendarDrag(CalendarSource.Inbox, item.Id));
        e.Handled = true;
        DragDrop.DoDragDrop(InboxItems, data, DragDropEffects.Move);
    }
}
