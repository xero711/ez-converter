using System.Text.Json;

namespace EZConverter.Sharing;

public sealed record TransferHistoryRecord(
    string EntryId,
    string Direction,
    string Name,
    long Completed,
    long Total,
    string State,
    DateTimeOffset FinishedAtUtc)
{
    public TransferProgress ToProgress() => new(EntryId, Direction, Name, Completed, Total, State);
}

public static class TransferHistoryStore
{
    public const int MaximumEntries = 100;
    private const long MaximumFileBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static bool IsTerminal(string state) => state is "完了" or "一部完了" or "送信済み" or "拒否" or "キャンセル" or "エラー" or "失敗" or "期限切れ";

    public static IReadOnlyList<TransferHistoryRecord> Load(string path)
    {
        if (!File.Exists(path)) return [];
        if (new FileInfo(path).Length > MaximumFileBytes) throw new InvalidDataException("転送履歴ファイルが大きすぎます。");
        var records = JsonSerializer.Deserialize<List<TransferHistoryRecord>>(File.ReadAllText(path)) ?? [];
        return records.Where(IsValid)
            .OrderByDescending(record => record.FinishedAtUtc)
            .Take(MaximumEntries)
            .ToArray();
    }

    public static void Save(string path, IEnumerable<TransferHistoryRecord> records)
    {
        var normalized = records.Where(IsValid)
            .GroupBy(record => record.EntryId, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(record => record.FinishedAtUtc).First())
            .OrderByDescending(record => record.FinishedAtUtc)
            .Take(MaximumEntries)
            .ToArray();
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ?? throw new ArgumentException("履歴の保存先が不正です。", nameof(path));
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, ".transfer-history-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(normalized, JsonOptions));
            File.Move(temporaryPath, fullPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static bool IsValid(TransferHistoryRecord? record) => record is not null &&
        Guid.TryParseExact(record.EntryId, "N", out _) &&
        !string.IsNullOrWhiteSpace(record.Direction) && record.Direction.Length <= 32 &&
        !string.IsNullOrWhiteSpace(record.Name) && record.Name.Length <= 128 &&
        !record.Name.Any(char.IsControl) && !record.Name.Contains('/') && !record.Name.Contains('\\') &&
        record.Completed >= 0 && record.Total >= 0 && record.Completed <= record.Total &&
        IsTerminal(record.State) && record.State.Length <= 32 &&
        record.FinishedAtUtc != default;
}
