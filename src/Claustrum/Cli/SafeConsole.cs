namespace Claustrum.Cli;

// A closed terminal (SIGHUP) makes every later console write throw EIO; on the stream echo's reader thread that is an
// unhandled exception (SIGABRT, no receipt), and in the final output it would turn a cancel's exit 130 into 1. The receipt
// is on disk before any of these writes (2026-10-14, found by review), so losing the line costs nothing but the line.
internal static class SafeConsole
{
    public static void WriteLine(TextWriter writer, string line)
    {
        try
        {
            writer.WriteLine(line);
        }
        catch (IOException)
        {
            // Nobody is reading.
        }
    }

    // A prompt's text, with no line break: the cursor stays after it for the reply.
    public static void Prompt(string text)
    {
        try
        {
            Console.Error.Write(text);
        }
        catch (IOException)
        {
            // Nobody is reading.
        }
    }

    public static void Out(string line = "") => WriteLine(Console.Out, line);

    public static void Error(string line) => WriteLine(Console.Error, line);
}
