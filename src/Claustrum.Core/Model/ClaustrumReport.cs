using System.Text.Json;
using System.Text.Json.Serialization;

namespace Claustrum.Core.Model;

// Data is null only when a claustrum-report fence was found but did not parse as JSON
// (ReportStatus.Unparsed); RawText is always the fence's raw content so callers keep the
// evidence either way. See NOTES.md "Report extraction shape".
public sealed record ClaustrumReport(JsonElement? Data, string RawText)
{
    public const string NeedsInputStatus = "needs_input";

    /// <summary>The report says <c>"status": "needs_input"</c>: the role stopped to ask its human something (#124 stage 3b).</summary>
    [JsonIgnore]
    public bool NeedsInput =>
        Data is { ValueKind: JsonValueKind.Object } data
        && data.TryGetProperty("status", out JsonElement status)
        && status.ValueKind == JsonValueKind.String
        && status.GetString() == NeedsInputStatus;

    /// <summary>
    /// The report's <c>questions</c>, leniently read: an entry with no prompt is skipped, a missing key becomes <c>q&lt;n&gt;</c>, missing
    /// options none, and a missing <c>allow_free_form</c> true, so a half-formed question can still be answered.
    /// </summary>
    [JsonIgnore]
    public ReportQuestion[] Questions
    {
        get
        {
            if (Data is not { ValueKind: JsonValueKind.Object } data
                || !data.TryGetProperty("questions", out JsonElement questions)
                || questions.ValueKind != JsonValueKind.Array)
                return [];

            List<ReportQuestion> read = [];
            foreach (JsonElement entry in questions.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object || TextOf(entry, "prompt") is not { Length: > 0 } prompt)
                    continue;

                string[] options = entry.TryGetProperty("options", out JsonElement list) && list.ValueKind == JsonValueKind.Array
                    ? [.. list.EnumerateArray().Where(option => option.ValueKind == JsonValueKind.String).Select(option => option.GetString() ?? "")]
                    : [];
                bool allowFreeForm = !entry.TryGetProperty("allow_free_form", out JsonElement free) || free.ValueKind != JsonValueKind.False;
                read.Add(new ReportQuestion(TextOf(entry, "key") is { Length: > 0 } key ? key : $"q{read.Count + 1}", prompt, options, allowFreeForm));
            }

            return [.. read];
        }
    }

    private static string? TextOf(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;
}
