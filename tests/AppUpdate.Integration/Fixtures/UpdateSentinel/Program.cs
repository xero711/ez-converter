var marker = Environment.GetEnvironmentVariable("EZCONVERTER_UPDATE_AGENT_MARKER");
if (!string.IsNullOrWhiteSpace(marker))
{
    await File.WriteAllTextAsync(marker, "updated app launched");
}
