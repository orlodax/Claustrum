using System.Text.Json;
using System.Text.RegularExpressions;
using Claustrum.Core.Model;

namespace Claustrum.Core.Report;

// Role-agnostic on purpose: Core may not reference Claustrum.Roles, and the fence's JSON shape
// varies per role (docs/PLAN.md B3) — this only finds and parses the fence, never validates it.
// A plain substring-style regex scan (not a CommonMark-correct fence parser) is what makes a
// claustrum-report fence nested inside a larger wrapping fence still get found as "the inner one".
public static partial class ReportExtractor
{
    public static ExtractedReport Extract(string text)
    {
        MatchCollection matches = FencePattern().Matches(text);
        if (matches.Count == 0)
            return new ExtractedReport(null, ReportStatus.Missing, []);

        string body = matches[^1].Groups["body"].Value.Trim();
        string[] warnings = matches.Count > 1
            ? [$"multiple claustrum-report fences found ({matches.Count}); using the last"]
            : [];

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            return new ExtractedReport(new ClaustrumReport(document.RootElement.Clone(), body), ReportStatus.Ok, warnings);
        }
        catch (JsonException)
        {
            return new ExtractedReport(new ClaustrumReport(null, body), ReportStatus.Unparsed, warnings);
        }
    }

    [GeneratedRegex("""`{3,}claustrum-report\s*\r?\n(?<body>.*?)\r?\n`{3,}""", RegexOptions.Singleline)]
    private static partial Regex FencePattern();
}
