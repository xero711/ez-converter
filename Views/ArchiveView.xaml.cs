using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using EZConverter.Compression;
using EZConverter.Compression.Models;
using MediaConverter.Models;
using Microsoft.Win32;
using UserControl = System.Windows.Controls.UserControl;
using MessageBox = System.Windows.MessageBox;
using DragEventArgs = System.Windows.DragEventArgs;

namespace MediaConverter.Views;

public partial class ArchiveView : UserControl
{
    private readonly ObservableCollection<string> _inputs = [];
    private Task<ArchiveService>? _serviceTask;
    private CancellationTokenSource? _cancel;
    private Task<ArchiveOperationResult>? _job;
    private string? _lastOutput;
    public ArchiveView()
    {
        InitializeComponent(); InputList.ItemsSource = _inputs;
        _inputs.CollectionChanged += (_, _) => CountText.Text = $"{_inputs.Count}件";
        OutputDirectoryBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "EZ Converter");
    }
    private async void View_Loaded(object sender, RoutedEventArgs e)
    {
        if (_serviceTask is not null) return;
        _serviceTask = ArchiveService.DetectAsync();
        try { var service = await _serviceTask; GpuStatusText.Text = service.Detection.Notes; }
        catch (Exception ex) { GpuStatusText.Text = "GPUを確認できませんでした: " + ex.Message; }
    }
    private void Options_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (FormatCombo is null || LevelCombo is null) return;
        FormatCombo.IsEnabled = LevelCombo.IsEnabled = ActionCombo.SelectedIndex == 0;
    }
    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Filter = ActionCombo.SelectedIndex == 1 ? "対応アーカイブ|*.zip;*.ziper|すべてのファイル|*.*" : "すべてのファイル|*.*" };
        if (picker.ShowDialog(Window.GetWindow(this)) == true) AddPaths(picker.FileNames);
    }
    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "圧縮するフォルダ", Multiselect = true };
        if (picker.ShowDialog(Window.GetWindow(this)) == true) AddPaths(picker.FolderNames);
    }
    private void AddPaths(IEnumerable<string> paths)
    {
        if (_cancel is not null) return;
        foreach (string path in paths) if ((File.Exists(path) || Directory.Exists(path)) && !_inputs.Contains(path, StringComparer.OrdinalIgnoreCase)) _inputs.Add(path);
    }
    public void PrepareFromExplorer(ExplorerArchiveLaunchRequest request)
    {
        ActionCombo.SelectedIndex = request.Extract ? 1 : 0;
        GpuCombo.SelectedIndex = 1;
        if (!request.Extract) FormatCombo.SelectedIndex = 1;
        AddPaths(request.Paths);
        if (_inputs.Count == 0) return;

        string firstPath = Path.GetFullPath(_inputs[0]);
        var outputDirectory = request.Extract || File.Exists(firstPath)
            ? Path.GetDirectoryName(firstPath)
            : Directory.GetParent(firstPath)?.FullName;
        if (!string.IsNullOrWhiteSpace(outputDirectory)) OutputDirectoryBox.Text = outputDirectory;
    }
    public void StartFromExplorer() => Start_Click(StartButton, new RoutedEventArgs());
    private void Remove_Click(object sender, RoutedEventArgs e) { foreach (var path in InputList.SelectedItems.Cast<string>().ToArray()) _inputs.Remove(path); }
    private void Clear_Click(object sender, RoutedEventArgs e) => _inputs.Clear();
    private void Files_DragOver(object sender, DragEventArgs e) { e.Effects = _cancel is null && e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop) ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None; e.Handled = true; }
    private void Files_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] paths)
        {
            AddPaths(paths);
        }

        e.Handled = true;
    }
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "保存先フォルダ" };
        if (picker.ShowDialog(Window.GetWindow(this)) == true) OutputDirectoryBox.Text = picker.FolderName;
    }
    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_cancel is not null) return;
        bool extracting = ActionCombo.SelectedIndex == 1;
        if (!ValidateInputs(extracting)) return;

        string? output = extracting ? null : SelectOutputFile();
        if (!extracting && output is null) return;

        await RunOperationAsync(extracting, output);
    }

    private bool ValidateInputs(bool extracting)
    {
        if (_inputs.Count == 0)
        {
            MessageBox.Show("ファイルまたはフォルダを追加してください。", "圧縮・展開");
            return false;
        }

        if (extracting && (_inputs.Count != 1 || !File.Exists(_inputs[0])))
        {
            MessageBox.Show("展開するZIPまたはZiper Fastを1つ選択してください。", "圧縮・展開");
            return false;
        }

        return true;
    }

    private string? SelectOutputFile()
    {
        bool fast = FormatCombo.SelectedIndex == 1;
        var picker = new Microsoft.Win32.SaveFileDialog
        {
            Filter = fast ? "Ziper Fast|*.ziper" : "ZIP|*.zip",
            DefaultExt = fast ? ".ziper" : ".zip",
            FileName = _inputs.Count == 1 ? Path.GetFileNameWithoutExtension(_inputs[0]) : "まとめて圧縮",
            OverwritePrompt = true
        };

        if (Directory.Exists(OutputDirectoryBox.Text)) picker.InitialDirectory = OutputDirectoryBox.Text;
        return picker.ShowDialog(Window.GetWindow(this)) == true ? picker.FileName : null;
    }

    private async Task RunOperationAsync(bool extracting, string? output)
    {
        _cancel = new CancellationTokenSource();
        SetBusy(true); ProgressBar.Value = 0; OpenResultButton.IsEnabled = false;
        var inputs = _inputs.ToArray();
        var mode = (GpuMode)GpuCombo.SelectedIndex;
        var level = (ArchiveCompressionLevel)LevelCombo.SelectedIndex;
        var ct = _cancel.Token;
        var parent = OutputDirectoryBox.Text;
        var progress = new Progress<ArchiveProgress>(p => { ProgressBar.Value = p.Percent; StatusText.Text = $"{p.Message}  {p.Percent:F0}%  ({p.ProcessedFiles:N0}/{p.TotalFiles:N0}件)"; });
        var log = new Progress<string>(s => { if (s.Contains("フォールバック") || s.Contains("CPUへ")) GpuStatusText.Text = s; });
        var reporter = new ArchiveOperationReporter(progress, log);
        try
        {
            var service = await (_serviceTask ??= ArchiveService.DetectAsync(ct));
            var operation = new ArchiveExecution
            {
                Extracting = extracting,
                Inputs = inputs,
                Output = output,
                Mode = mode,
                Level = level,
                ParentDirectory = parent,
                CancellationToken = ct,
                Progress = progress,
                Log = log,
                Reporter = reporter
            };
            _job = StartArchiveJob(service, operation);
            var result = await _job;
            ShowSuccess(result, service);
        }
        catch (OperationCanceledException) { StatusText.Text = "キャンセルしました。元のファイルは変更していません。"; }
        catch (Exception ex) { StatusText.Text = "処理できませんでした。"; MessageBox.Show(ex.Message, "圧縮・展開", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { _job = null; _cancel.Dispose(); _cancel = null; SetBusy(false); }
    }

    private static Task<ArchiveOperationResult> StartArchiveJob(ArchiveService service, ArchiveExecution operation)
    {
        if (operation.Extracting)
        {
            return Task.Run(() =>
            {
                var destination = AvailableDirectory(operation.ParentDirectory, Path.GetFileNameWithoutExtension(operation.Inputs[0]));
                return service.ExtractAsync(operation.Inputs[0], destination, operation.Mode, operation.Progress, operation.Log, operation.CancellationToken);
            }, operation.CancellationToken);
        }

        return Task.Run(() => service.CreateAsync(operation.Inputs, operation.Output!, operation.Mode, operation.Level, operation.Reporter, operation.CancellationToken, overwrite: true), operation.CancellationToken);
    }

    private void ShowSuccess(ArchiveOperationResult result, ArchiveService service)
    {
        _lastOutput = result.OutputPath;
        OpenResultButton.IsEnabled = true;
        ProgressBar.Value = 100;
        StatusText.Text = $"完了  {result.TotalFiles:N0}件 · {result.Engine} · {result.Duration.TotalSeconds:F1}秒";
        GpuStatusText.Text = result.UsedFallback ? result.FallbackReason : service.Detection.GpuName ?? "CPUで処理しました。";
    }

    private sealed record ArchiveExecution
    {
        public required bool Extracting { get; init; }
        public required string[] Inputs { get; init; }
        public required string? Output { get; init; }
        public required GpuMode Mode { get; init; }
        public required ArchiveCompressionLevel Level { get; init; }
        public required string ParentDirectory { get; init; }
        public required CancellationToken CancellationToken { get; init; }
        public required IProgress<ArchiveProgress> Progress { get; init; }
        public required IProgress<string> Log { get; init; }
        public required ArchiveOperationReporter Reporter { get; init; }
    }

    private static string AvailableDirectory(string parent, string name)
    {
        string root = Path.GetFullPath(parent);
        string path = Path.Combine(root, name);
        int index = 2;
        while (Directory.Exists(path) || File.Exists(path))
        {
            path = Path.Combine(root, $"{name} ({index})");
            index++;
        }

        return path;
    }
    private void SetBusy(bool busy) { OptionsPanel.IsEnabled = InputToolbar.IsEnabled = DestinationPanel.IsEnabled = DropSurface.IsEnabled = StartButton.IsEnabled = !busy; CancelButton.IsEnabled = busy; }
    private async void Cancel_Click(object sender, RoutedEventArgs e) => await CancelOperationAsync();
    private async Task CancelOperationAsync()
    {
        var cancellation = _cancel;
        if (cancellation is not null) await cancellation.CancelAsync();
    }
    public async Task StopAsync()
    {
        await CancelOperationAsync();

        var job = _job;
        if (job is not null)
        {
            try { await job; } catch (Exception) { /* Start handler displays/cleans the result. */ }
        }
    }
    private void OpenResult_Click(object sender, RoutedEventArgs e)
    {
        if (_lastOutput is null) return;
        string directory = Directory.Exists(_lastOutput) ? _lastOutput : Path.GetDirectoryName(_lastOutput)!;
        try { Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true }); } catch (Exception ex) { MessageBox.Show(ex.Message, "保存先を開く"); }
    }
}
