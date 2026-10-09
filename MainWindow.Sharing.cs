using System.ComponentModel;

namespace MediaConverter;

public partial class MainWindow
{
    private readonly HashSet<string> _convertedForSharing = new(StringComparer.OrdinalIgnoreCase);
    private void RememberConvertedForSharing(string path)
    {
        _convertedForSharing.Add(path);
        ShareConvertedButton.IsEnabled = true;
    }
    private void ShareConverted_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        SharingPanel.AddPaths(_convertedForSharing.Where(System.IO.File.Exists));
        SharingTabItem.IsSelected = true;
    }
    private bool _sharingClosed;
    private async void MainWindow_ClosingForSharing(object? sender, CancelEventArgs e)
    {
        if (_sharingClosed) return;
        e.Cancel = true;
        IsEnabled = false;
        try { await ArchivePanel.StopAsync(); await SharingPanel.DisposeAsync(); }
        finally { _sharingClosed = true; Close(); }
    }
}
