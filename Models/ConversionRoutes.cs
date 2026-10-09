namespace MediaConverter.Models;

public static class ConversionRoutes
{
    private static readonly HashSet<string> SpreadsheetFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "csv", "xls", "xlsx", "ods"
    };

    private static readonly HashSet<string> WriterFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "abw", "dbk", "doc", "docm", "docx", "dot", "dotm", "dotx",
        "html", "odt", "rtf", "sxw", "txt", "wps"
    };

    private static readonly HashSet<string> ArchiveOutputFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "7z", "jar", "tar", "tar.7z", "tar.bz", "tar.gz", "tar.xz", "tbz2", "tgz", "zip"
    };

    public static ConversionBackend? Resolve(MediaFormat source, MediaFormat target)
    {
        if (!source.CanRead || !target.CanWrite)
        {
            return null;
        }

        var sameExtension = source.Extension.Equals(target.Extension, StringComparison.OrdinalIgnoreCase);
        var sameCodec = string.Equals(source.VideoCodec, target.VideoCodec, StringComparison.OrdinalIgnoreCase);
        if (sameExtension && sameCodec)
        {
            return null;
        }

        if (target.VideoCodec is not null)
        {
            return source.Kind == MediaKind.Video && target.Kind == MediaKind.Video
                && source.Backend == ConversionBackend.Ffmpeg && target.Backend == ConversionBackend.Ffmpeg
                    ? ConversionBackend.Ffmpeg
                    : null;
        }

        if (source.Extension.Equals("pdf", StringComparison.OrdinalIgnoreCase))
        {
            return target.Extension is "xlsx" or "txt" ? ConversionBackend.PdfText : null;
        }

        if (source.Backend == ConversionBackend.LibreOffice)
        {
            if (source.Kind == MediaKind.Presentation)
            {
                return target.Kind == MediaKind.Presentation || target.Extension == "pdf"
                    ? ConversionBackend.LibreOffice : null;
            }

            if (source.Kind == MediaKind.Document)
            {
                if (SpreadsheetFormats.Contains(source.Extension))
                {
                    return SpreadsheetFormats.Contains(target.Extension) || target.Extension == "pdf"
                        ? ConversionBackend.LibreOffice : null;
                }

                if (WriterFormats.Contains(source.Extension))
                {
                    return WriterFormats.Contains(target.Extension) || target.Extension == "pdf"
                        ? ConversionBackend.LibreOffice : null;
                }
            }

            return null;
        }

        if (source.Kind == MediaKind.Archive)
        {
            return target.Kind == MediaKind.Archive && ArchiveOutputFormats.Contains(target.Extension)
                ? ConversionBackend.SevenZip : null;
        }

        if (source.Kind == MediaKind.Video && target.Kind == MediaKind.Audio)
        {
            return source.Backend == ConversionBackend.Ffmpeg && target.Backend == ConversionBackend.Ffmpeg
                ? ConversionBackend.Ffmpeg : null;
        }

        return source.Kind == target.Kind && source.Backend == target.Backend
            ? source.Backend : null;
    }
}
