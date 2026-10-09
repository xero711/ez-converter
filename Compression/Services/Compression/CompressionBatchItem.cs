namespace EZConverter.Compression.Services.Compression;

public sealed record CompressionBatchItem(
    string Name,
    ReadOnlyMemory<byte> Input);
