using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DesktopPlanner.App;

// A standalone window: neither the tray popup nor a desktop widget owns its lifetime.
internal sealed class ResetConfirmationWindow : Window
{
    public ResetConfirmationWindow()
    {
        Title = "Сбросить все данные";
        Width = 460; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true; ShowActivated = true;
        Background = new SolidColorBrush(Color.FromRgb(35, 47, 63));
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "Удалить все данные виджетов?", FontSize = 20,
            FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Margin = new Thickness(0, 16, 0, 20), TextWrapping = TextWrapping.Wrap,
            Text = "Будут удалены все задачи, включая выполненные, события календаря, входящие события и заметки.\n\nОтменить сброс нельзя. Расположение и настройки виджетов сохранятся." });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Отмена", IsCancel = true, IsDefault = true, MinWidth = 100 };
        var confirm = new Button { Content = "Удалить все данные", MinWidth = 170 };
        confirm.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(confirm); panel.Children.Add(buttons);
        Content = panel;
        ContentRendered += (_, _) => { Activate(); cancel.Focus(); };
    }
}
