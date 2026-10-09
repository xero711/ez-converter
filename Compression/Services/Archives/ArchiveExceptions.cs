namespace EZConverter.Compression.Services.Archives;

public sealed class ArchiveSecurityException : Exception
{
    public ArchiveSecurityException(string message) : base(message)
    {
    }
}

public sealed class UnsupportedZipException : Exception
{
    public UnsupportedZipException(string message) : base(message)
    {
    }
}
