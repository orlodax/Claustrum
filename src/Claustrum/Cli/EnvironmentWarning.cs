using System.Text.Json;
using Claustrum.Core.Backends;
using Claustrum.Core.Json;
using Claustrum.Core.Model;

namespace Claustrum.Cli;

// #145: the one stderr line a front door that hands back a receipt prints when the job's environment got in its way (`run` and the doors
// that print its receipt, `coordinate`, `jobs show`, `jobs wait`, `jobs follow`). One line whatever the count; the full list is the receipt's.
// The receipt is read leniently, as `jobs list` reads it: a kind this binary does not know is still printed.
internal static class EnvironmentWarning
{
    private const int Shown = 3;
    private const int WhereChars = 80;

    public static void Print(EnvironmentIssue[]? issues)
    {
        if (Line(issues) is { } line)
            SafeConsole.Error(line);
    }

    /// <summary>The same line for a receipt read off disk: <paramref name="receipt"/> is the whole result.json document.</summary>
    public static void PrintReceipt(JsonElement receipt) =>
        PrintArray(receipt.ValueKind == JsonValueKind.Object && receipt.TryGetProperty("environment_issues", out JsonElement issues) ? issues : null);

    /// <summary>The line for an <c>environment_issues</c> array as JSON: null, or anything that is not an array, prints nothing.</summary>
    public static void PrintArray(JsonElement? issues)
    {
        if (Line(issues) is { } line)
            SafeConsole.Error(line);
    }

    public static string? Line(EnvironmentIssue[]? issues) =>
        Format(issues?.Select(issue => new Entry(KindName(issue.Kind), issue.Count, issue.Where)).ToArray());

    public static string? Line(JsonElement? issues) =>
        issues is { ValueKind: JsonValueKind.Array } array ? Format([.. array.EnumerateArray().Where(entry => entry.ValueKind == JsonValueKind.Object).Select(EntryOf)]) : null;

    private static string? Format(Entry[]? entries)
    {
        if (entries is not { Length: > 0 })
            return null;

        string listed = string.Join("; ", entries.Take(Shown).Select(Describe));
        string more = entries.Length > Shown ? $"; +{entries.Length - Shown} more" : "";
        return $"warning: environment issues ({entries.Length}): {listed}{more}. Stop and tell the user before using this result.";
    }

    // The where is the agent's own text (a command, a path): sanitized like every stream line, and cut short to keep the one line.
    private static string Describe(Entry entry)
    {
        string text = $"{StreamText.Sanitize(entry.Kind)} ×{entry.Count}";
        return entry.Where is { Length: > 0 } where ? $"{text} at {StreamText.Sanitize(where, WhereChars)}" : text;
    }

    private static Entry EntryOf(JsonElement issue) => new(
        issue.TryGetProperty("kind", out JsonElement kind) && kind.ValueKind == JsonValueKind.String ? kind.GetString() ?? "?" : "?",
        issue.TryGetProperty("count", out JsonElement count) && count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out int number) ? number : 1,
        issue.TryGetProperty("where", out JsonElement where) && where.ValueKind == JsonValueKind.String ? where.GetString() : null);

    private static string KindName(EnvironmentIssueKind kind) =>
        JsonSerializer.Serialize(kind, ClaustrumJsonContext.Default.EnvironmentIssueKind).Trim('"');

    private sealed record Entry(string Kind, int Count, string? Where);
}
