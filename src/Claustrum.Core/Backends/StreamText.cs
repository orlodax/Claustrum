using System.Globalization;
using System.Text;

namespace Claustrum.Core.Backends;

// Every string a readable formatter prints comes from a backend, a model or a file on disk, so it
// is untrusted terminal input: an ESC sequence in it retitles the window or erases the line above
// (2026-10-15 review of #127). Raw mode shows such bytes JSON-escaped; this does the same for the
// readable path.
public static class StreamText
{
    public const int TextLimit = 140;

    // Whitespace runs (newlines included) fold to one space; every other control character
    // (C0, DEL, C1) becomes a visible escape; printable Unicode passes unchanged. Past maxLength
    // the rest is cut and marked with an ellipsis.
    public static string Sanitize(string? text, int maxLength = int.MaxValue)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        StringBuilder builder = new(Math.Min(text.Length, 160));
        bool pendingSpace = false;

        foreach (char ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (builder.Length >= maxLength)
                return builder.Append('…').ToString();

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            if (ch is < '\u0020' or '\u007f')
                builder.Append(CultureInfo.InvariantCulture, $"\\x{(int)ch:x2}");
            else if (ch is >= '\u0080' and <= '\u009f')
                builder.Append(CultureInfo.InvariantCulture, $"\\u{(int)ch:x4}");
            else
                builder.Append(ch);
        }

        return builder.ToString();
    }

    // A line's own time when the stream carries one; otherwise the arrival time live, and a
    // placeholder for a replay, where "now" would be a made-up time (claude's stream has no stamps).
    public static string Time(long? timestampMs, bool replayed) => timestampMs is { } ms and >= -62135596800000 and <= 253402300799999
        ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)
        : replayed ? "--:--:--" : DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
}
