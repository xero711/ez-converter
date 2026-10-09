namespace EZConverter.Compression.Models;

public sealed record ArchiveProgress(
    long TotalBytes,
    long ProcessedBytes,
    int TotalFiles,
    int ProcessedFiles,
    string CurrentPath,
    string Message)
{
    public double Percent
    {
        get
        {
            if (TotalBytes <= 0)
            {
                return ProcessedFiles >= TotalFiles && TotalFiles > 0 ? 100 : 0;
            }

            return Math.Clamp((double)ProcessedBytes / TotalBytes * 100, 0, 100);
        }
    }
}
