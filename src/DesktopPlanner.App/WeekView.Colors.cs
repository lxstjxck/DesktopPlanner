using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using DesktopPlanner.Domain;
using Forms = System.Windows.Forms;
namespace DesktopPlanner.App;

public partial class WeekView
{
    private static Color ParseEventColor(string? hex)
    {
        if (hex is { Length: 7 } && hex[0] == '#' && hex.Skip(1).All(Uri.IsHexDigit))
            return (Color)ColorConverter.ConvertFromString(hex);
        return Color.FromRgb(173, 150, 210);
    }
    private MenuItem CreateColorMenu(CalendarEvent item)
    {
        var menu = new MenuItem { Header = "Цвет" };
        foreach (var (name, hex) in new (string Name, string? Hex)[]
        {
            ("По умолчанию", null), ("Красный", "#D64C59"), ("Оранжевый", "#EEA04B"),
            ("Жёлтый", "#EBCD65"), ("Зелёный", "#68BB8B"), ("Бирюзовый", "#54BDBF"),
            ("Синий", "#467CD1"), ("Фиолетовый", "#9A75D2"), ("Розовый", "#D886B5"), ("Серый", "#7C8B9D")
        })
        {
            var choice = new MenuItem { Header = name, IsCheckable = true, IsChecked = string.Equals(item.ColorHex, hex, StringComparison.OrdinalIgnoreCase),
                Icon = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(ParseEventColor(hex)) } };
            choice.Click += async (_, _) => { if (model is not null) await model.SetColorAsync(item.Id, hex); };
            menu.Items.Add(choice);
        }
        menu.Items.Add(new Separator());
        var custom = new MenuItem { Header = "Другой цвет…" };
        custom.Click += async (_, _) =>
        {
            var calendar = model; var owner = Window.GetWindow(this);
            if (calendar is null || owner is null) return;
            var color = ParseEventColor(item.ColorHex);
            using var picker = new Forms.ColorDialog { FullOpen = true, Color = System.Drawing.Color.FromArgb(color.R, color.G, color.B) };
            if (picker.ShowDialog(new ColorPickerOwner(new WindowInteropHelper(owner).Handle)) == Forms.DialogResult.OK)
                await calendar.SetColorAsync(item.Id, $"#{picker.Color.R:X2}{picker.Color.G:X2}{picker.Color.B:X2}");
        };
        menu.Items.Add(custom); return menu;
    }
    private sealed record ColorPickerOwner(nint Handle) : Forms.IWin32Window;
}
