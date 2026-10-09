using System.IO;

namespace EZConverter.Compression.Services.Archives;

public static class ZipEncryptionDetector
{
    private const uint EndOfCentralDirectorySignature = 0x06054b50;
    private const uint Zip64EndOfCentralDirectoryLocatorSignature = 0x07064b50;
    private const uint Zip64EndOfCentralDirectorySignature = 0x06064b50;
    private const uint CentralDirectoryFileHeaderSignature = 0x02014b50;
    private const ushort EncryptedFlag = 0x0001;

    public static bool HasEncryptedEntries(string archivePath)
    {
        try
        {
            using var stream = File.OpenRead(archivePath);
            if (stream.Length < 22)
            {
                return false;
            }

            if (!TryFindCentralDirectory(stream, out var centralDirectoryOffset, out var centralDirectorySize))
            {
                return false;
            }

            stream.Seek(centralDirectoryOffset, SeekOrigin.Begin);
            var endPosition = centralDirectoryOffset + centralDirectorySize;
            Span<byte> header = stackalloc byte[46];

            while (stream.Position + header.Length <= stream.Length && stream.Position < endPosition)
            {
                ReadExactly(stream, header);
                if (ReadUInt32(header, 0) != CentralDirectoryFileHeaderSignature)
                {
                    return false;
                }

                var flags = ReadUInt16(header, 8);
                if ((flags & EncryptedFlag) != 0)
                {
                    return true;
                }

                var fileNameLength = ReadUInt16(header, 28);
                var extraLength = ReadUInt16(header, 30);
                var commentLength = ReadUInt16(header, 32);
                stream.Seek(fileNameLength + extraLength + commentLength, SeekOrigin.Current);
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool TryFindCentralDirectory(Stream stream, out long offset, out long size)
    {
        offset = 0;
        size = 0;

        var searchLength = (int)Math.Min(stream.Length, ushort.MaxValue + 22L);
        var buffer = new byte[searchLength];
        stream.Seek(-searchLength, SeekOrigin.End);
        ReadExactly(stream, buffer);

        for (var i = buffer.Length - 22; i >= 0; i--)
        {
            if (ReadUInt32(buffer, i) != EndOfCentralDirectorySignature)
            {
                continue;
            }

            size = ReadUInt32(buffer, i + 12);
            offset = ReadUInt32(buffer, i + 16);

            if (offset != uint.MaxValue && size != uint.MaxValue)
            {
                return true;
            }

            var eocdAbsoluteOffset = stream.Length - searchLength + i;
            return TryReadZip64CentralDirectory(stream, eocdAbsoluteOffset, out offset, out size);
        }

        return false;
    }

    private static bool TryReadZip64CentralDirectory(Stream stream, long eocdAbsoluteOffset, out long offset, out long size)
    {
        offset = 0;
        size = 0;

        if (eocdAbsoluteOffset < 20)
        {
            return false;
        }

        Span<byte> locator = stackalloc byte[20];
        stream.Seek(eocdAbsoluteOffset - 20, SeekOrigin.Begin);
        ReadExactly(stream, locator);
        if (ReadUInt32(locator, 0) != Zip64EndOfCentralDirectoryLocatorSignature)
        {
            return false;
        }

        var zip64EocdOffset = (long)ReadUInt64(locator, 8);
        if (zip64EocdOffset < 0 || zip64EocdOffset + 56 > stream.Length)
        {
            return false;
        }

        Span<byte> zip64Header = stackalloc byte[56];
        stream.Seek(zip64EocdOffset, SeekOrigin.Begin);
        ReadExactly(stream, zip64Header);
        if (ReadUInt32(zip64Header, 0) != Zip64EndOfCentralDirectorySignature)
        {
            return false;
        }

        size = (long)ReadUInt64(zip64Header, 40);
        offset = (long)ReadUInt64(zip64Header, 48);
        return offset >= 0 && size >= 0;
    }

    private static void ReadExactly(Stream stream, Span<byte> buffer)
    {
        var readTotal = 0;
        while (readTotal < buffer.Length)
        {
            var read = stream.Read(buffer[readTotal..]);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            readTotal += read;
        }
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> buffer, int offset) =>
        BitConverter.ToUInt32(buffer.Slice(offset, 4));

    private static ushort ReadUInt16(ReadOnlySpan<byte> buffer, int offset) =>
        BitConverter.ToUInt16(buffer.Slice(offset, 2));

    private static ulong ReadUInt64(ReadOnlySpan<byte> buffer, int offset) =>
        BitConverter.ToUInt64(buffer.Slice(offset, 8));
}
