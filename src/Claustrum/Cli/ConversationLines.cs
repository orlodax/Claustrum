using Claustrum.Core.Backends;
using Claustrum.Core.Live;
using Claustrum.Core.Report;

namespace Claustrum.Cli;

// How a live job's conversation reads on a terminal (#124): `? ` a role's question, `> ` the human's reply or message,
// `! ` a Claustrum notice, `· ` a role's progress note (`claustrum-note: …`, stage 3b). Everything shown comes from a file a role's text reached, so each line goes through the
// stream sanitizer (control characters become visible escapes); line breaks survive, a continuation line is indented.
internal static class ConversationLines
{
    /// <summary>The entry's lines, or none for one that is not shown: the role's own messages are already in the stream, its notes excepted.</summary>
    public static IEnumerable<string> Render(ConversationEntry entry)
    {
        if (IsNote(entry))
            return Render("·", entry.Text[NoteRecorder.Prefix.Length..].Trim());

        return Marker(entry) is { } marker ? Render(marker, entry.Text) : [];
    }

    public static IEnumerable<string> Render(string marker, string text)
    {
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd().Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            string line = StreamText.Sanitize(lines[index]);
            yield return index == 0 ? $"{marker} {line}" : line.Length == 0 ? "" : $"  {line}";
        }
    }

    // NoteRecorder keeps the prefix in the text, which is what tells a note from a live turn's message (also a Message from the Role).
    private static bool IsNote(ConversationEntry entry) =>
        entry is { From: ConversationParty.Role, Kind: ConversationKind.Message } && entry.Text.StartsWith(NoteRecorder.Prefix, StringComparison.Ordinal) && !entry.Text.Contains('\n', StringComparison.Ordinal);

    private static string? Marker(ConversationEntry entry) => entry switch
    {
        { Kind: ConversationKind.Notice } or { From: ConversationParty.Claustrum } => "!",
        { Kind: ConversationKind.Question, From: ConversationParty.Role } => "?",
        { From: ConversationParty.Human } => ">",
        _ => null,
    };
}
