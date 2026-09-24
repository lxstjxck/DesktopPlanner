using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopPlanner.Domain;
using DesktopPlanner.Infrastructure;
namespace DesktopPlanner.App;

public sealed record GlassBackdrop(ScreenRectangle Bounds, ImageSource Image);
public sealed class GlassBackdropService
{
    public IReadOnlyList<GlassBackdrop> Backdrops { get; private set; } = [];
    public async Task LoadAsync()
    {
        Backdrops = await Task.Run(() =>
        {
            var result = new List<GlassBackdrop>();
            try
            {
                var infos = DesktopWallpaperReader.Read();
                var virtualBounds = new Rect(infos.Min(i => i.Bounds.Left), infos.Min(i => i.Bounds.Top),
                    infos.Max(i => i.Bounds.Right) - infos.Min(i => i.Bounds.Left), infos.Max(i => i.Bounds.Bottom) - infos.Min(i => i.Bounds.Top));
                foreach (var info in infos)
                {
                    var width = info.Bounds.Right - info.Bounds.Left; var height = info.Bounds.Bottom - info.Bounds.Top;
                    var drawing = new DrawingGroup();
                    using (var context = drawing.Open())
                    {
                        var color = Color.FromRgb((byte)info.Background, (byte)(info.Background >> 8), (byte)(info.Background >> 16));
                        context.DrawRectangle(new SolidColorBrush(color), null, new Rect(0, 0, width, height));
                        if (File.Exists(info.Path))
                        {
                            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
                            image.UriSource = new Uri(info.Path); image.EndInit(); image.Freeze();
                            var target = info.Position == 5 ? new Rect(virtualBounds.X - info.Bounds.Left, virtualBounds.Y - info.Bounds.Top, virtualBounds.Width, virtualBounds.Height) : new Rect(0, 0, width, height);
                            var scale = info.Position == 3 ? Math.Min(target.Width / image.PixelWidth, target.Height / image.PixelHeight) : Math.Max(target.Width / image.PixelWidth, target.Height / image.PixelHeight);
                            var rect = info.Position == 2 ? target : new Rect(target.X + (target.Width - image.PixelWidth * scale) / 2, target.Y + (target.Height - image.PixelHeight * scale) / 2, image.PixelWidth * scale, image.PixelHeight * scale);
                            if (info.Position == 0) rect = new Rect((width - image.PixelWidth) / 2, (height - image.PixelHeight) / 2, image.PixelWidth, image.PixelHeight);
                            if (info.Position == 1)
                                context.DrawRectangle(new ImageBrush(image) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, image.PixelWidth, image.PixelHeight) }, null, target);
                            else context.DrawImage(image, rect);
                        }
                    }
                    drawing.ClipGeometry = new RectangleGeometry(new Rect(0, 0, width, height));
                    var source = new DrawingImage(drawing); source.Freeze(); result.Add(new(info.Bounds, source));
                }
            }
            catch (Exception ex) { Serilog.Log.Warning(ex, "Wallpaper unavailable; using glass fallback"); }
            return (IReadOnlyList<GlassBackdrop>)result;
        });
    }
}
