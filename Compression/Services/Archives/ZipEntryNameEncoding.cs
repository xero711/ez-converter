using System.Buffers.Binary;
using System.Text;

namespace EZConverter.Compression.Services.Archives;

internal static class ZipEntryNameEncoding
{
    private const uint CentralDirectoryHeaderSignature = 0x02014b50;
    private const uint EndOfCentralDirectorySignature = 0x06054b50;
    private const uint Zip64EndOfCentralDirectorySignature = 0x06064b50;
    private const uint Zip64EndOfCentralDirectoryLocatorSignature = 0x07064b50;
    private const ushort Utf8Flag = 0x0800;
    private const int EndOfCentralDirectoryMinimumSize = 22;
    private const int MaximumZipCommentBytes = ushort.MaxValue;
    private const int CentralDirectoryHeaderSize = 46;
    private static readonly Encoding LegacyWindowsJapaneseEncoding = CreateLegacyWindowsJapaneseEncoding();
    private static readonly Encoding StrictLegacyWindowsJapaneseEncoding = CreateStrictLegacyWindowsJapaneseEncoding();
    private static readonly Encoding LegacyZipStandardEncoding = CreateLegacyZipStandardEncoding();

    public static Encoding Utf8 { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    // ZIPのUTF-8フラグがない日本語Windows ZIPは、CP932で保存されていることが多い。
    public static Encoding LegacyWindowsJapanese => LegacyWindowsJapaneseEncoding;

    public static Encoding LegacyZipStandard => LegacyZipStandardEncoding;

    public static Encoding? GetReadEntryNameEncoding(Stream stream)
    {
        if (!stream.CanSeek)
        {
            return null;
        }

        var originalPosition = stream.Position;
        try
        {
            if (!TryGetCentralDirectory(stream, out var offset, out var size))
            {
                return null;
            }

            return DetectEntryNameEncoding(stream, offset, size) switch
            {
                EntryNameEncodingKind.Utf8 => Utf8,
                EntryNameEncodingKind.LegacyWindowsJapanese => LegacyWindowsJapanese,
                EntryNameEncodingKind.LegacyZipStandard => LegacyZipStandard,
                _ => null
            };
        }
        catch (IOException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
        catch (OverflowException)
        {
            return null;
        }
        finally
        {
            stream.Position = originalPosition;
        }
    }

    private static EntryNameEncodingKind DetectEntryNameEncoding(Stream stream, long offset, long size)
    {
        if (size <= 0 || offset < 0 || offset > stream.Length || size > stream.Length - offset)
        {
            return EntryNameEncodingKind.Unknown;
        }

        var centralDirectoryEnd = checked(offset + size);
        var entryCount = 0;
        var hasJapaneseEntryName = false;
        var canUseLegacyWindowsJapaneseEncoding = true;
        var hasUtf8EntryName = false;
        var header = new byte[CentralDirectoryHeaderSize];
        stream.Position = offset;

        while (stream.Position < centralDirectoryEnd)
        {
            if (centralDirectoryEnd - stream.Position < CentralDirectoryHeaderSize)
            {
                return EntryNameEncodingKind.Unknown;
            }

            stream.ReadExactly(header);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != CentralDirectoryHeaderSignature)
            {
                return EntryNameEncodingKind.Unknown;
            }

            var flags = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8));
            if ((flags & Utf8Flag) != 0)
            {
                hasUtf8EntryName = true;
            }

            var fileNameLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28));
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(30));
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(32));
            var remainingLength = checked((long)fileNameLength + extraLength + commentLength);
            if (remainingLength > centralDirectoryEnd - stream.Position)
            {
                return EntryNameEncodingKind.Unknown;
            }

            var fileNameBytes = new byte[fileNameLength];
            stream.ReadExactly(fileNameBytes);
            if (!hasUtf8EntryName && fileNameBytes.Any(static value => value >= 0x80))
            {
                try
                {
                    var decodedName = StrictLegacyWindowsJapaneseEncoding.GetString(fileNameBytes);
                    if (!StrictLegacyWindowsJapaneseEncoding.GetBytes(decodedName).AsSpan().SequenceEqual(fileNameBytes))
                    {
                        canUseLegacyWindowsJapaneseEncoding = false;
                    }
                    else
                    {
                        hasJapaneseEntryName |= decodedName.Any(IsJapaneseCharacter);
                    }
                }
                catch (DecoderFallbackException)
                {
                    canUseLegacyWindowsJapaneseEncoding = false;
                }
            }

            stream.Position += extraLength + commentLength;
            entryCount++;
        }

        if (entryCount == 0 || stream.Position != centralDirectoryEnd)
        {
            return EntryNameEncodingKind.Unknown;
        }

        if (hasUtf8EntryName)
        {
            return EntryNameEncodingKind.Utf8;
        }

        return canUseLegacyWindowsJapaneseEncoding && hasJapaneseEntryName
            ? EntryNameEncodingKind.LegacyWindowsJapanese
            : EntryNameEncodingKind.LegacyZipStandard;
    }

    private static bool TryGetCentralDirectory(Stream stream, out long offset, out long size)
    {
        offset = 0;
        size = 0;
        if (stream.Length < EndOfCentralDirectoryMinimumSize)
        {
            return false;
        }

        var searchLength = checked((int)Math.Min(
            stream.Length,
            EndOfCentralDirectoryMinimumSize + MaximumZipCommentBytes));
        var buffer = new byte[searchLength];
        stream.Position = stream.Length - searchLength;
        stream.ReadExactly(buffer);

        for (var index = buffer.Length - EndOfCentralDirectoryMinimumSize; index >= 0; index--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(index)) != EndOfCentralDirectorySignature)
            {
                continue;
            }

            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(index + 20));
            if (index + EndOfCentralDirectoryMinimumSize + commentLength != buffer.Length)
            {
                continue;
            }

            var eocdOffset = stream.Length - searchLength + index;
            var centralDirectorySize = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(index + 12));
            var centralDirectoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(index + 16));
            if (centralDirectorySize != uint.MaxValue && centralDirectoryOffset != uint.MaxValue)
            {
                offset = centralDirectoryOffset;
                size = centralDirectorySize;
                return true;
            }

            return TryReadZip64CentralDirectory(stream, eocdOffset, out offset, out size);
        }

        return false;
    }

    private static bool TryReadZip64CentralDirectory(Stream stream, long eocdOffset, out long offset, out long size)
    {
        offset = 0;
        size = 0;
        if (eocdOffset < 20)
        {
            return false;
        }

        var locator = new byte[20];
        stream.Position = eocdOffset - locator.Length;
        stream.ReadExactly(locator);
        if (BinaryPrimitives.ReadUInt32LittleEndian(locator) != Zip64EndOfCentralDirectoryLocatorSignature)
        {
            return false;
        }

        var zip64EndOffset = BinaryPrimitives.ReadUInt64LittleEndian(locator.AsSpan(8));
        if (zip64EndOffset > long.MaxValue || zip64EndOffset > (ulong)(stream.Length - 56))
        {
            return false;
        }

        var header = new byte[56];
        stream.Position = (long)zip64EndOffset;
        stream.ReadExactly(header);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Zip64EndOfCentralDirectorySignature)
        {
            return false;
        }

        var centralDirectorySize = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(40));
        var centralDirectoryOffset = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(48));
        if (centralDirectorySize > long.MaxValue || centralDirectoryOffset > long.MaxValue)
        {
            return false;
        }

        offset = (long)centralDirectoryOffset;
        size = (long)centralDirectorySize;
        return true;
    }

    private static Encoding CreateLegacyWindowsJapaneseEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(932);
    }

    private static Encoding CreateStrictLegacyWindowsJapaneseEncoding() =>
        Encoding.GetEncoding(
            932,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);

    private static Encoding CreateLegacyZipStandardEncoding() =>
        Encoding.GetEncoding(437);

    private static bool IsJapaneseCharacter(char value) =>
        value is >= '\u3040' and <= '\u30ff' ||
        value is >= '\u3400' and <= '\u4dbf' ||
        value is >= '\u4e00' and <= '\u9fff' ||
        value is >= '\uff61' and <= '\uff9f';

    private enum EntryNameEncodingKind
    {
        Unknown,
        Utf8,
        LegacyWindowsJapanese,
        LegacyZipStandard
    }
}
