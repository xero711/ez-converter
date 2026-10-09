namespace MediaConverter.Models;

public sealed record ExplorerArchiveLaunchRequest(bool Extract, IReadOnlyList<string> Paths)
{
    public static ExplorerArchiveLaunchRequest? TryParse(IEnumerable<string> arguments)
    {
        var tokens = arguments.ToArray();
        var operationIndex = Array.FindIndex(tokens, token =>
            string.Equals(token, "--archive-compress", StringComparison.OrdinalIgnoreCase)
            || string.Equals(token, "--archive-extract", StringComparison.OrdinalIgnoreCase));
        if (operationIndex < 0)
        {
            return null;
        }

        var extract = string.Equals(tokens[operationIndex], "--archive-extract", StringComparison.OrdinalIgnoreCase);
        var paths = tokens.Skip(operationIndex + 1)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();
        return paths.Length == 0 ? null : new ExplorerArchiveLaunchRequest(extract, paths);
    }
}
