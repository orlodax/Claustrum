using System.Text;

namespace Claustrum.Cli;

// Windows encodes Console.Out with the console code page (437 on a GitHub runner), so every
// non-ASCII character this CLI prints — the em dashes all through `backends doctor --probe` — goes
// out as '?'; Unix is UTF-8 whatever the locale. Measured 2026-09-23: the six em-dash assertions in
// the copilot and opencode/api auth tests were the only ones in those files that failed, and only
// on windows-latest. A redirected stream gets a writer of our own rather than a code page change:
// setting Console.OutputEncoding calls SetConsoleOutputCP, which mutates the caller's console and
// outlives this process — too much for `claustrum ... > out.txt` to do behind someone's back.
public static class ConsoleEncoding
{
    public static void ForceUtf8()
    {
        UTF8Encoding encoding = new(encoderShouldEmitUTF8Identifier: false);

        // A real console is the one case that does need the code page itself: our own writer would
        // emit UTF-8 bytes the console would then render as mojibake. This also covers stderr,
        // whose encoding on Windows is Console.OutputEncoding too.
        if (!Console.IsOutputRedirected || !Console.IsErrorRedirected)
            Console.OutputEncoding = encoding;

        if (Console.IsOutputRedirected)
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), encoding) { AutoFlush = true });

        if (Console.IsErrorRedirected)
            Console.SetError(new StreamWriter(Console.OpenStandardError(), encoding) { AutoFlush = true });
    }
}
