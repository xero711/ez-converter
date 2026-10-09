using System.Buffers.Binary;

namespace EZConverter.Compression.Services.Archives;

internal sealed class Crc32
{
    private static readonly uint[][] Tables = CreateTables();
    private uint _value = 0xffffffff;

    public void Update(ReadOnlySpan<byte> buffer)
    {
        var crc = _value;
        while (buffer.Length >= 8)
        {
            crc ^= BinaryPrimitives.ReadUInt32LittleEndian(buffer);
            var high = BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..]);
            crc =
                Tables[7][(byte)crc] ^
                Tables[6][(byte)(crc >> 8)] ^
                Tables[5][(byte)(crc >> 16)] ^
                Tables[4][(byte)(crc >> 24)] ^
                Tables[3][(byte)high] ^
                Tables[2][(byte)(high >> 8)] ^
                Tables[1][(byte)(high >> 16)] ^
                Tables[0][(byte)(high >> 24)];
            buffer = buffer[8..];
        }

        var table = Tables[0];
        foreach (var b in buffer)
        {
            crc = table[(byte)(crc ^ b)] ^ (crc >> 8);
        }

        _value = crc;
    }

    public uint GetCurrentHash() => _value ^ 0xffffffff;

    private static uint[][] CreateTables()
    {
        var tables = new uint[8][];
        tables[0] = CreateTable();
        for (var tableIndex = 1; tableIndex < tables.Length; tableIndex++)
        {
            tables[tableIndex] = new uint[256];
            for (var value = 0; value < tables[tableIndex].Length; value++)
            {
                var previous = tables[tableIndex - 1][value];
                tables[tableIndex][value] = tables[0][(byte)previous] ^ (previous >> 8);
            }
        }

        return tables;
    }

    private static uint[] CreateTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) == 1 ? 0xedb88320 ^ (value >> 1) : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }
}
