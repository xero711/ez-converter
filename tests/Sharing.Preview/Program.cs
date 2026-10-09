using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EZConverter.Sharing;
using MediaConverter;
using MediaConverter.Services;
using MediaConverter.Views;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        try
        {
            var app = new App();
            app.InitializeComponent();
            var directory = Path.Combine(Directory.GetCurrentDirectory(), "work", "sharing-ui-preview");
            Directory.CreateDirectory(directory);
            foreach (var theme in new[] { "Dark", "Light" })
            {
                typeof(ThemeManager).GetMethod("Apply" + theme + "Theme", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [app.Resources]);
                var view = new SharingView { Width = 1080, Height = 800 };
                var canvas = new System.Windows.Controls.Border { Width = 1080, Height = 800, Background = (Brush)app.Resources["CardBackgroundBrush"], Child = view };
                view.AddPaths([Path.Combine(Directory.GetCurrentDirectory(), "README.md")]);
                view.Transfers.Add(new SharingView.TransferRow(new TransferProgress("preview-progress", "受信", "sample.bin", 45, 100, "受信中")));
                typeof(SharingView).GetMethod("ShowPage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, ["History"]);
                canvas.Measure(new Size(1080, 800));
                canvas.Arrange(new Rect(0, 0, 1080, 800));
                canvas.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);
                Console.WriteLine("PASS: " + theme + " history progress row renders with a read-only Percent binding");
                var image = new RenderTargetBitmap(1080, 800, 96, 96, PixelFormats.Pbgra32);
                image.Render(canvas);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using (var output = File.Create(Path.Combine(directory, theme.ToLowerInvariant() + ".png"))) encoder.Save(output);
                view.DisposeAsync().AsTask().GetAwaiter().GetResult();
                Console.WriteLine("RENDERED " + theme);
            }
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }
}
