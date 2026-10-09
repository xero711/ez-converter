using System.IO.Compression;

namespace EZConverter.Compression.Services.Compression;

public static class GpuDeflateSelfTest
{
    public static IReadOnlyList<byte[]> CreateValidationInputs()
    {
        return new[]
        {
            CreatePattern(32 * 1024, 17),
            CreatePattern(4096, 91)
        };
    }

    public static bool ValidateRawDeflateBatch(
        IReadOnlyList<ReadOnlyMemory<byte>> inputs,
        IReadOnlyList<ReadOnlyMemory<byte>> compressedItems,
        out string? error)
    {
        error = null;
        if (inputs.Count != compressedItems.Count)
        {
            error = "GPU自己検証で返された件数が入力件数と一致しません。";
            return false;
        }

        for (var index = 0; index < inputs.Count; index++)
        {
            if (!TryDecompressRawDeflate(compressedItems[index], out var decompressed, out var decompressError))
            {
                error = $"GPU自己検証項目 {index} は有効なraw DEFLATEではありません: {decompressError}";
                return false;
            }

            if (!inputs[index].Span.SequenceEqual(decompressed))
            {
                error = $"GPU自己検証項目 {index} の展開結果が元データと一致しません。";
                return false;
            }
        }

        return true;
    }

    private static bool TryDecompressRawDeflate(ReadOnlyMemory<byte> compressed, out byte[] decompressed, out string? error)
    {
        decompressed = Array.Empty<byte>();
        error = null;
        try
        {
            using var input = OpenReadOnlyMemoryStream(compressed);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            deflate.CopyTo(output);
            decompressed = output.ToArray();
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static MemoryStream OpenReadOnlyMemoryStream(ReadOnlyMemory<byte> memory)
    {
        return System.Runtime.InteropServices.MemoryMarshal.TryGetArray(memory, out var segment) && segment.Array is not null
            ? new MemoryStream(segment.Array, segment.Offset, segment.Count, writable: false)
            : new MemoryStream(memory.ToArray(), writable: false);
    }

    private static byte[] CreatePattern(int length, int seed)
    {
        var bytes = new byte[length];
        var state = (uint)(0xA5A55A5A + seed);
        for (var index = 0; index < bytes.Length; index++)
        {
            state = unchecked(state * 1664525u + 1013904223u);
            bytes[index] = index % 5 == 0
                ? (byte)(state >> 24)
                : (byte)((index * 31 + seed) % 251);
        }

        return bytes;
    }
}
