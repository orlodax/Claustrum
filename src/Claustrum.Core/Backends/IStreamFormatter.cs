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
    /// null when nothing should be printed. A live line.
    /// </summary>
    string? FormatLine(string rawLine);

    /// <summary>
    /// Same, with <paramref name="replayed"/> marking a line written before this process started
    /// watching: with no time of its own it prints a placeholder, not the current time.
    /// </summary>
    string? FormatLine(string rawLine, bool replayed);
}
