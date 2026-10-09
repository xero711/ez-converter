namespace MediaConverter.Models;

public enum ConversionBackend
{
    ImageMagick,
    Ffmpeg,
    LibreOffice,
    SevenZip,
    Calibre,
    FontForge,
    YtDlp,
    PdfText
}

public sealed record MediaFormat(
    string Extension,
    string DisplayName,
    MediaKind Kind,
    ConversionBackend Backend,
    bool CanRead = true,
    bool CanWrite = true,
    string? VideoCodec = null)
{
    public override string ToString() => $"[{KindLabel}] {DisplayName} (.{Extension})";

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
        _ => "その他"
    };
}
