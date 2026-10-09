using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EZConverter.Sharing;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;

namespace MediaConverter.Views;

public sealed class ShareQrWindow : Window
{
    public ShareQrWindow(string url)
    {
        Title = "共有URLのQRコード";
        Width = 440;
        Height = 620;
        MinWidth = 420;
        MinHeight = 580;
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var image = new Image
        {
            Source = LoadImage(ShareQrCode.CreatePng(url)),
            Width = 320,
            Height = 320,
            Stretch = Stretch.Uniform,
            SnapsToDevicePixels = true,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);

        var urlBox = new TextBox
        {
            Text = url,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 72,
            Margin = new Thickness(0, 8, 0, 10),
            Padding = new Thickness(10)
        };
        var status = new TextBlock
        {
            Text = "リンクを知っている人はアクセスできます。必要に応じて共有パスワードを設定してください。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 12),
            Foreground = (Brush)Application.Current.FindResource("MutedTextBrush")
        };
        var copy = new Button { Content = "URLをコピー", Margin = new Thickness(0, 0, 8, 0), Style = (Style)Application.Current.FindResource("PrimaryButton") };
        var close = new Button { Content = "閉じる" };
        copy.Click += (_, _) =>
        {
            try { System.Windows.Clipboard.SetText(url); status.Text = "URLをコピーしました。"; }
            catch (System.Runtime.InteropServices.COMException) { status.Text = "コピーできませんでした。URLを選択してコピーしてください。"; }
        };
        close.Click += (_, _) => Close();

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        actions.Children.Add(copy);
        actions.Children.Add(close);

        var content = new StackPanel { Margin = new Thickness(24) };
        content.Children.Add(new TextBlock
        {
            Text = "スマートフォンで読み取る",
            FontSize = 21,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.FindResource("TitleForegroundBrush"),
            Margin = new Thickness(0, 0, 0, 12)
        });
        content.Children.Add(image);
        content.Children.Add(urlBox);
        content.Children.Add(status);
        content.Children.Add(actions);
        Content = new Border
        {
            Background = (Brush)Application.Current.FindResource("WindowBackgroundBrush"),
            Child = content
        };
    }

    private static BitmapImage LoadImage(byte[] png)
    {
        using var stream = new MemoryStream(png);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
