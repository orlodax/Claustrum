namespace Claustrum.Core.Backends;

/// <summary>
/// Turns one raw stream line into zero or one human-readable progress lines for stderr echo and
/// `jobs follow`. A backend with no formatter returns the raw line unchanged (pass-through).
/// Bookkeeping events (step boundaries, heartbeats, rate-limit events) return null.
/// </summary>
public interface IStreamFormatter
{
    /// <summary>
    /// Formats one raw line from the backend's JSON event stream. Returns the formatted line, or
    /// null when nothing should be printed. Allocation-light: called on every stream line.
    /// </summary>
    string? FormatLine(string rawLine);
}
