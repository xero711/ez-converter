namespace EZConverter.Compression.Services.Compression;

public sealed record CompressionBatchResult(ReadOnlyMemory<byte> CompressedBytes);
