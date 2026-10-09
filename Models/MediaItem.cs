using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace MediaConverter.Models;

public sealed class MediaItem : INotifyPropertyChanged
{
    private string _status = "待機中";
    private int _progress;
    private string _message = string.Empty;
    private MediaFormat? _selectedOutputFormat;

    public MediaItem(string sourcePath)
    {
        SourcePath = sourcePath;
        FileName = Path.GetFileName(sourcePath);
        SourceFormat = MediaFormatCatalog.FindByPath(sourcePath);
        Kind = SourceFormat?.Kind ?? MediaKind.Unknown;
        AvailableOutputFormats = SourceFormat is null
            ? []
            : MediaFormatCatalog.GetOutputFormats(SourceFormat);
        _selectedOutputFormat = SourceFormat is null
            ? null
            : MediaFormatCatalog.GetRecommendedOutputFormat(SourceFormat);
    }

    public string SourcePath { get; }
    public string FileName { get; }
    public MediaFormat? SourceFormat { get; }
    public IReadOnlyList<MediaFormat> AvailableOutputFormats { get; }
    public MediaKind Kind { get; }
    public string KindLabel => Kind switch
    {
        MediaKind.Image => "画像",
        MediaKind.Vector => "ベクター",
        MediaKind.Video => "動画",
        MediaKind.Audio => "音声",
        MediaKind.Document => "文書",
        MediaKind.Presentation => "プレゼン",
        MediaKind.Archive => "アーカイブ",
        MediaKind.EBook => "電子書籍",
        MediaKind.Font => "フォント",
        _ => "不明"
    };

    public string Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    public int Progress
    {
        get => _progress;
        set => SetField(ref _progress, value);
    }

    public string Message
    {
        get => _message;
        set => SetField(ref _message, value);
    }

    public MediaFormat? SelectedOutputFormat
    {
        get => _selectedOutputFormat;
        set => SetField(ref _selectedOutputFormat, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
