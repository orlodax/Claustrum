using System.Text;

namespace Claustrum.Core.Jobs;

// A job log is read while ProcessRunner still holds it open for writing, so the reader must share
// Write in turn: with the .NET default FileShare.Read, Windows threw "The process cannot access the
// file … stdout.log because it is being used by another process" on every running job (2026-09-22,
// PR #26 windows leg). NOTES.md "Reading a live job log needs FileShare.ReadWrite" has the detail.
public static class JobLog
{
    public static IEnumerable<string> ReadLines(string path)
    {
        using StreamReader reader = Open(path);
        while (reader.ReadLine() is { } line)
            yield return line;
    }

    public static string ReadAllText(string path)
    {
        using StreamReader reader = Open(path);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// The log's last line, or null when the file does not exist yet. Best-effort: on a running job
    /// the line read back may be mid-write, which is fine for a progress indicator.
    /// </summary>
    public static string? LastLine(string path) =>
        File.Exists(path) ? ReadLines(path).LastOrDefault() : null;

    private static StreamReader Open(string path) =>
        new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
}
