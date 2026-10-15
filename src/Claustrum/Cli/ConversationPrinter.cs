using Claustrum.Core.Backends;
using Claustrum.Core.Live;

namespace Claustrum.Cli;

// Prints each conversation entry once, to stderr beside the stream lines (`jobs follow`, and `run`/`coordinate` on a
// terminal). The file is re-read whole on every poll, so what was already shown is told apart by Seq.
internal sealed class ConversationPrinter
{
    private int shownThrough = -1;

    public void Print(IReadOnlyList<ConversationEntry> entries, string? prefix)
    {
        int seen = shownThrough;
        foreach (ConversationEntry entry in entries.Where(entry => entry.Seq > seen))
        {
            shownThrough = Math.Max(shownThrough, entry.Seq);
            foreach (string line in ConversationLines.Render(entry))
                SafeConsole.Error(prefix is null ? line : $"{StreamText.Sanitize(prefix)} {line}");
        }
    }
}
