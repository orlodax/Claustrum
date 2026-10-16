using System.Text.Json;

namespace Claustrum.Core.Backends.Claude;

// What a claude session had cost before the run that resumes it (#127 review; measured 2026-10-16, NOTES.md "Final review of #127", finding 2).
// `--resume` keeps the session id and restores its running total into `total_cost_usd`, so the run's own cost is that total minus what the
// session had already reached. The receipt's `raw` is claude's own result, whose total is the session's even when its job was charged only its
// share; `cost_usd` is the fallback for a receipt that kept no `raw`.
internal static class ClaudeSessionCost
{
    // The highest total any receipt in the store records for the session. A running total never goes down, so the highest is the latest, which
    // job ids (second resolution) cannot say; it is also the parent job's of an answer continuation unless another run resumed the session since.
    public static decimal? Before(string jobsRoot, string currentJobDirectory, string sessionId)
    {
        string current = Path.GetFileName(Path.TrimEndingDirectorySeparator(currentJobDirectory));
        string[] directories;
        try
        {
            directories = Directory.GetDirectories(jobsRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        decimal? highest = null;
        foreach (string directory in directories)
        {
            if (Path.GetFileName(directory) != current && TotalIn(directory, sessionId) is { } total && total > (highest ?? -1m))
                highest = total;
        }

        return highest;
    }

    private static decimal? TotalIn(string directory, string sessionId)
    {
        string text;
        try
        {
            text = File.ReadAllText(Path.Combine(directory, "result.json"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        // Most receipts belong to another session: the id is looked for as text before the document is parsed.
        if (!text.Contains(sessionId, StringComparison.Ordinal))
            return null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || ClaudeBackend.TryGetString(root, "session_id") != sessionId)
                return null;

            return root.TryGetProperty("raw", out JsonElement raw) && raw.ValueKind == JsonValueKind.Object && TryGetDecimal(raw, "total_cost_usd") is { } total
                ? total
                : TryGetDecimal(root, "cost_usd");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static decimal? TryGetDecimal(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out JsonElement property) && property.ValueKind == JsonValueKind.Number && property.TryGetDecimal(out decimal value) ? value : null;
}
