using System.Text.Json;

namespace Claustrum.Core.Model;

// Data is null only when a claustrum-report fence was found but did not parse as JSON
// (ReportStatus.Unparsed); RawText is always the fence's raw content so callers keep the
// evidence either way. See NOTES.md "Report extraction shape".
public sealed record ClaustrumReport(JsonElement? Data, string RawText);
