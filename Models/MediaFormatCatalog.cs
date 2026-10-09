using System.IO;

namespace MediaConverter.Models;

public static class MediaFormatCatalog
{
    public static readonly IReadOnlyList<MediaFormat> ImageFormats =
    [
        F("avif", "AVIF", MediaKind.Image), F("bmp", "BMP", MediaKind.Image), F("cur", "CUR", MediaKind.Image),
        F("dds", "DDS", MediaKind.Image), F("dng", "DNG", MediaKind.Image, readOnly: true), F("exr", "OpenEXR", MediaKind.Image),
        F("fax", "FAX", MediaKind.Image), F("fits", "FITS", MediaKind.Image), F("gif", "GIF", MediaKind.Image),
        F("hdr", "Radiance HDR", MediaKind.Image), F("heic", "HEIC", MediaKind.Image), F("heif", "HEIF", MediaKind.Image),
        F("ico", "ICO", MediaKind.Image), F("jbg", "JBG", MediaKind.Image), F("jbig", "JBIG", MediaKind.Image),
        F("jfi", "JFI", MediaKind.Image), F("jfif", "JFIF", MediaKind.Image), F("jif", "JIF", MediaKind.Image),
        F("jp2", "JPEG 2000", MediaKind.Image), F("jpe", "JPE", MediaKind.Image), F("jpeg", "JPEG", MediaKind.Image),
        F("jpg", "JPEG", MediaKind.Image), F("jps", "JPS", MediaKind.Image), F("map", "MAP", MediaKind.Image),
        F("mng", "MNG", MediaKind.Image), F("mtv", "MTV", MediaKind.Image), F("otb", "OTB", MediaKind.Image),
        F("pam", "PAM", MediaKind.Image), F("pbm", "PBM", MediaKind.Image), F("pcd", "PCD", MediaKind.Image),
        F("pcx", "PCX", MediaKind.Image), F("pfm", "PFM", MediaKind.Image), F("pgm", "PGM", MediaKind.Image),
        F("pgx", "PGX", MediaKind.Image), F("picon", "PICON", MediaKind.Image), F("pict", "PICT", MediaKind.Image),
        F("png", "PNG", MediaKind.Image), F("pnm", "PNM", MediaKind.Image), F("ppm", "PPM", MediaKind.Image),
        F("psd", "Photoshop PSD", MediaKind.Image), F("ras", "Sun Raster", MediaKind.Image), F("rgb", "RGB", MediaKind.Image),
        F("rgba", "RGBA", MediaKind.Image), F("sgi", "SGI", MediaKind.Image), F("sixel", "SIXEL", MediaKind.Image),
        F("sun", "SUN", MediaKind.Image), F("tga", "TGA", MediaKind.Image), F("tif", "TIFF", MediaKind.Image),
        F("tiff", "TIFF", MediaKind.Image), F("uyvy", "UYVY", MediaKind.Image), F("viff", "VIFF", MediaKind.Image),
        F("wbmp", "WBMP", MediaKind.Image), F("webp", "WebP", MediaKind.Image), F("xbm", "XBM", MediaKind.Image),
        F("xpm", "XPM", MediaKind.Image), F("xwd", "XWD", MediaKind.Image), F("yuv", "YUV", MediaKind.Image)
    ];

    public static readonly IReadOnlyList<MediaFormat> RawImageFormats =
    [
        F("3fr", "Hasselblad RAW", MediaKind.Image, readOnly: true), F("arw", "Sony RAW", MediaKind.Image, readOnly: true),
        F("cr2", "Canon RAW", MediaKind.Image, readOnly: true), F("crw", "Canon CIFF RAW", MediaKind.Image, readOnly: true),
        F("dcr", "Kodak RAW", MediaKind.Image, readOnly: true), F("erf", "Epson RAW", MediaKind.Image, readOnly: true),
        F("iiq", "Phase One RAW", MediaKind.Image, readOnly: true), F("k25", "Kodak DC25 RAW", MediaKind.Image, readOnly: true),
        F("kdc", "Kodak RAW", MediaKind.Image, readOnly: true), F("mef", "Mamiya RAW", MediaKind.Image, readOnly: true),
        F("mrw", "Minolta RAW", MediaKind.Image, readOnly: true), F("nef", "Nikon RAW", MediaKind.Image, readOnly: true),
        F("nrw", "Nikon Compact RAW", MediaKind.Image, readOnly: true), F("orf", "Olympus RAW", MediaKind.Image, readOnly: true),
        F("pef", "Pentax RAW", MediaKind.Image, readOnly: true), F("raf", "Fujifilm RAW", MediaKind.Image, readOnly: true),
        F("rw2", "Panasonic RAW", MediaKind.Image, readOnly: true), F("sr2", "Sony RAW 2", MediaKind.Image, readOnly: true),
        F("srf", "Sony RAW", MediaKind.Image, readOnly: true), F("x3f", "Sigma RAW", MediaKind.Image, readOnly: true)
    ];

    public static readonly IReadOnlyList<MediaFormat> VectorFormats =
    [
        F("ai", "Adobe Illustrator", MediaKind.Vector, readOnly: true), F("cgm", "CGM", MediaKind.Vector),
        F("cdr", "CorelDRAW", MediaKind.Vector, readOnly: true), F("cdt", "CorelDRAW Template", MediaKind.Vector, readOnly: true),
        F("cmx", "Corel Presentation", MediaKind.Vector, readOnly: true), F("emf", "EMF", MediaKind.Vector),
        F("eps", "EPS", MediaKind.Vector), F("fig", "Xfig", MediaKind.Vector), F("odg", "ODG", MediaKind.Vector),
        F("plt", "HPGL PLT", MediaKind.Vector), F("ps", "PostScript", MediaKind.Vector), F("sk", "Skencil", MediaKind.Vector, readOnly: true),
        F("sk1", "sK1", MediaKind.Vector, readOnly: true), F("svg", "SVG", MediaKind.Vector), F("wmf", "WMF", MediaKind.Vector)
    ];

    public static readonly IReadOnlyList<MediaFormat> VideoFormats =
    [
        V("3g2", "3G2"), V("3gp", "3GP"), V("asf", "ASF"), V("av1", "AV1", readOnly: true), V("avi", "AVI"),
        V("divx", "DivX"), V("f4v", "F4V"), V("flv", "Flash Video"), V("hevc", "HEVC", readOnly: true), V("m2ts", "M2TS"),
        V("m2v", "MPEG-2 Video"), V("m4v", "M4V"), V("mjpeg", "Motion JPEG"), V("mkv", "Matroska"),
        V("mod", "MOD", readOnly: true), V("mov", "QuickTime MOV"), V("mp4", "MPEG-4"), V("mpeg", "MPEG"),
        V("mpeg2", "MPEG-2", readOnly: true), V("mpg", "MPG"), V("mts", "MTS"), V("mxf", "MXF"), V("ogv", "OGV"),
        V("rm", "RealMedia"), V("rmvb", "RealMedia VBR", readOnly: true), V("swf", "SWF"), V("tod", "TOD", readOnly: true),
        V("ts", "MPEG Transport Stream"), V("vob", "VOB"), V("webm", "WebM"), V("wmv", "Windows Media Video"),
        V("wtv", "Windows Recorded TV"), V("xvid", "Xvid")
    ];

    public static readonly MediaFormat Av1Mp4OutputFormat = new(
        "mp4", "AV1動画（MP4）", MediaKind.Video, ConversionBackend.Ffmpeg,
        CanRead: false, CanWrite: true, VideoCodec: "libaom-av1");

    public static readonly IReadOnlyList<MediaFormat> AudioFormats =
    [
        A("8svx", "8SVX", readOnly: true), A("aac", "AAC"), A("ac3", "AC3"), A("aif", "AIFF"), A("aiff", "AIFF"),
        A("amb", "AMB", readOnly: true), A("amr", "AMR"), A("ape", "APE", readOnly: true), A("au", "AU"), A("avr", "AVR", readOnly: true),
        A("caf", "CAF"), A("cdda", "CDDA", readOnly: true), A("cvs", "CVS", readOnly: true), A("cvsd", "CVSD", readOnly: true), A("cvu", "CVU", readOnly: true),
        A("dss", "DSS", readOnly: true), A("dts", "DTS", readOnly: true), A("flac", "FLAC"), A("gsm", "GSM"), A("hcom", "HCOM", readOnly: true),
        A("ima", "IMA ADPCM", readOnly: true), A("ircam", "IRCAM"), A("m4a", "M4A"), A("m4r", "M4R"), A("maud", "MAUD", readOnly: true),
        A("ma4", "M4A (ma4入力)", readOnly: true), A("mp2", "MP2"), A("mp3", "MP3"), A("oga", "OGA"), A("ogg", "OGG"),
        A("opus", "Opus"), A("ra", "RealAudio"), A("sln", "SLN", readOnly: true), A("snd", "SND", readOnly: true), A("sou", "SOU", readOnly: true),
        A("sph", "SPHERE", readOnly: true), A("spx", "Speex"), A("tak", "TAK", readOnly: true), A("tta", "TTA"), A("txw", "TXW", readOnly: true),
        A("voc", "Creative Voice"), A("vox", "VOX", readOnly: true), A("vqf", "VQF", readOnly: true), A("w64", "Wave64"),
        A("wav", "WAV"), A("wma", "WMA"), A("wv", "WavPack"), A("wve", "WVE", readOnly: true), A("xa", "XA", readOnly: true)
    ];

    public static readonly IReadOnlyList<MediaFormat> DocumentFormats =
    [
        D("abw", "AbiWord", readOnly: true), D("csv", "CSV"), D("dbk", "DocBook XML", readOnly: true), D("doc", "Word DOC"),
        D("docm", "Word DOCM"), D("docx", "Word DOCX"), D("dot", "Word DOT"), D("dotm", "Word DOTM"), D("dotx", "Word DOTX"),
        D("html", "HTML"), D("odt", "OpenDocument Text"), D("pdf", "PDF"), D("rtf", "RTF"),
        D("sxw", "StarOffice Writer", readOnly: true), D("txt", "Text"), D("wps", "Works WPS", readOnly: true), D("xls", "Excel XLS"),
        D("xlsx", "Excel XLSX"), D("ods", "OpenDocument Spreadsheet")
    ];

    public static readonly IReadOnlyList<MediaFormat> PresentationFormats =
    [
        P("odp", "OpenDocument Presentation"), P("pot", "PowerPoint POT"), P("potm", "PowerPoint POTM"),
        P("potx", "PowerPoint POTX"), P("pps", "PowerPoint PPS"), P("ppsm", "PowerPoint PPSM", readOnly: true),
        P("ppsx", "PowerPoint PPSX"), P("ppt", "PowerPoint PPT"), P("pptm", "PowerPoint PPTM"), P("pptx", "PowerPoint PPTX")
    ];

    public static readonly IReadOnlyList<MediaFormat> ArchiveFormats =
    [
        R("7z", "7-Zip"), R("ace", "ACE", readOnly: true), R("alz", "ALZ", readOnly: true), R("arj", "ARJ", readOnly: true),
        R("arc", "ARC", readOnly: true), R("bz2", "BZip2", readOnly: true), R("cab", "CAB", readOnly: true), R("cpio", "CPIO", readOnly: true),
        R("deb", "Debian Package", readOnly: true), R("gz", "GZip", readOnly: true), R("jar", "JAR"), R("lha", "LHA", readOnly: true),
        R("lzh", "LZH", readOnly: true), R("rar", "RAR", readOnly: true), R("rpm", "RPM", readOnly: true), R("tar", "TAR"),
        R("tar.7z", "TAR.7Z"), R("tar.bz", "TAR.BZ"), R("tar.gz", "TAR.GZ"), R("tar.lz", "TAR.LZ", readOnly: true),
        R("tar.lzma", "TAR.LZMA", readOnly: true), R("tar.lzo", "TAR.LZO", readOnly: true), R("tar.xz", "TAR.XZ"), R("tar.z", "TAR.Z", readOnly: true),
        R("tbz2", "TBZ2"), R("tgz", "TGZ"), R("xz", "XZ", readOnly: true), R("zip", "ZIP")
    ];

    public static readonly IReadOnlyList<MediaFormat> EBookFormats =
    [
        E("azw3", "AZW3"), E("epub", "EPUB"), E("fb2", "FB2"), E("lrf", "LRF"), E("mobi", "MOBI"),
        E("pdb", "PDB"), E("rb", "Rocket eBook"), E("snb", "SNB"), E("tcr", "TCR")
    ];

    public static readonly IReadOnlyList<MediaFormat> FontFormats =
    [
        G("afm", "AFM", readOnly: true), G("cff", "CFF"), G("cid", "CID", readOnly: true), G("dfont", "DFONT"),
        G("otf", "OpenType OTF"), G("pfa", "PostScript PFA", readOnly: true), G("pfb", "PostScript PFB"),
        G("ps", "PostScript Font", readOnly: true), G("sfd", "SplineFont Database"), G("t11", "Type 11", readOnly: true),
        G("t42", "Type 42", readOnly: true), G("ttf", "TrueType TTF"), G("ufo", "UFO", readOnly: true),
        G("woff", "Web Open Font"), G("woff2", "Web Open Font 2")
    ];

    public static readonly IReadOnlyList<MediaFormat> AllFormats =
        ImageFormats.Concat(RawImageFormats).Concat(VectorFormats).Concat(VideoFormats).Concat(AudioFormats)
            .Concat(DocumentFormats).Concat(PresentationFormats).Concat(ArchiveFormats).Concat(EBookFormats).Concat(FontFormats)
            .GroupBy(format => format.Extension, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

    public static readonly IReadOnlyList<MediaFormat> OutputFormats = AllFormats.Where(format => format.CanWrite).ToArray();

    public static IReadOnlyList<MediaFormat> GetOutputFormats(MediaFormat sourceFormat)
    {
        var formats = OutputFormats
            .Where(format => ConversionRoutes.Resolve(sourceFormat, format) is not null)
            .ToList();

        if (sourceFormat.Kind == MediaKind.Video && sourceFormat.Backend == ConversionBackend.Ffmpeg
            && ConversionRoutes.Resolve(sourceFormat, Av1Mp4OutputFormat) is not null)
        {
            formats.Add(Av1Mp4OutputFormat);
        }

        return formats
            .OrderBy(format => GetFormatPriority(format.Extension))
            .ThenBy(format => format.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(format => format.Extension, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static MediaFormat? GetRecommendedOutputFormat(MediaFormat sourceFormat)
    {
        var preferredExtension = sourceFormat.Kind switch
        {
            MediaKind.Image => "png",
            MediaKind.Vector => "svg",
            MediaKind.Video => "mp4",
            MediaKind.Audio => "mp3",
            MediaKind.Document => sourceFormat.Extension == "pdf" ? "xlsx" : "pdf",
            MediaKind.Presentation => "pdf",
            MediaKind.Archive => "zip",
            MediaKind.EBook => "epub",
            MediaKind.Font => "ttf",
            _ => sourceFormat.Extension
        };

        var outputs = GetOutputFormats(sourceFormat);
        return outputs.FirstOrDefault(format =>
                   format.Extension.Equals(preferredExtension, StringComparison.OrdinalIgnoreCase)
                   && format.VideoCodec is null)
            ?? outputs.FirstOrDefault(format => format.VideoCodec is null)
            ?? outputs.FirstOrDefault();
    }

    private static int GetFormatPriority(string extension) => extension.ToLowerInvariant() switch
    {
        "png" or "mp4" or "mp3" or "pdf" or "zip" or "epub" or "ttf" or "svg" => 0,
        "jpg" or "jpeg" or "webp" or "mkv" or "wav" or "docx" or "pptx" or "xlsx" => 1,
        _ => 2
    };

    public static MediaFormat? FindByPath(string path)
    {
        var fileName = Path.GetFileName(path);
        return AllFormats
            .Where(format => format.CanRead && fileName.EndsWith("." + format.Extension, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(format => format.Extension.Length)
            .FirstOrDefault();
    }

    public static MediaFormat? FindByExtension(string extension)
    {
        var normalized = extension.TrimStart('.').ToLowerInvariant();
        if (normalized == "ma4")
        {
            normalized = "m4a";
        }

        return OutputFormats.FirstOrDefault(format => format.Extension.Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }

    public static MediaKind GetKindFromPath(string path) => FindByPath(path)?.Kind ?? MediaKind.Unknown;

    public static string GetOpenFileDialogFilter()
    {
        var patterns = AllFormats.Where(format => format.CanRead).Select(format => "*." + format.Extension);
        return $"対応ファイル|{string.Join(';', patterns)}|すべてのファイル|*.*";
    }

    private static MediaFormat F(string extension, string name, MediaKind kind, bool readOnly = false) =>
        new(extension, name, kind, ConversionBackend.ImageMagick, CanWrite: !readOnly);

    private static MediaFormat V(string extension, string name, bool readOnly = false) =>
        new(extension, name, MediaKind.Video, ConversionBackend.Ffmpeg, CanWrite: !readOnly);

    private static MediaFormat A(string extension, string name, bool readOnly = false) =>
        new(extension, name, MediaKind.Audio, ConversionBackend.Ffmpeg, CanWrite: !readOnly);

    private static MediaFormat D(string extension, string name, bool readOnly = false) =>
        new(extension, name, MediaKind.Document, ConversionBackend.LibreOffice, CanWrite: !readOnly);

    private static MediaFormat P(string extension, string name, bool readOnly = false) =>
        new(extension, name, MediaKind.Presentation, ConversionBackend.LibreOffice, CanWrite: !readOnly);

    private static MediaFormat R(string extension, string name, bool readOnly = false) =>
        new(extension, name, MediaKind.Archive, ConversionBackend.SevenZip, CanWrite: !readOnly);

    private static MediaFormat E(string extension, string name, bool readOnly = false) =>
        new(extension, name, MediaKind.EBook, ConversionBackend.Calibre, CanWrite: !readOnly);

    private static MediaFormat G(string extension, string name, bool readOnly = false) =>
        new(extension, name, MediaKind.Font, ConversionBackend.FontForge, CanWrite: !readOnly);
}
