using System.Text;
using Claustrum.Core.IO;
using Claustrum.Roles.Sync;

namespace Claustrum.Roles;

// #55: the api backend has no file access, so "read AGENTS.md" cannot be obeyed there; the files ride in the
// system prompt instead. The content goes to a third-party API, hence the fence: a file whose real path leaves
// the cwd is never read, and the check is made on the OPENED handle, not the path (NOTES.md "The api backend gets
// AGENTS.md and CLAUDE.md inlined").
internal static class ApiHouseRules
{
    public const int CapBytes = 32 * 1024;

    private const string ReadInstruction = "Read `AGENTS.md` and `CLAUDE.md` in the working directory first; they are law";
    private const string InlineInstruction = "The repository's rules follow; they are law";

    private static readonly string[] fileNames = ["AGENTS.md", "CLAUDE.md"];

    /// <summary>
    /// <paramref name="houseRules"/> with its read instruction replaced and the cwd's rule files appended under
    /// their own headings; what could not be inlined as it should is named in <paramref name="warnings"/>.
    /// </summary>
    public static string Compose(string houseRules, string cwd, List<string> warnings)
    {
        if (!houseRules.Contains(ReadInstruction, StringComparison.Ordinal))
            throw new RoleRenderException("_shared/house-rules.md no longer carries the read instruction the api rendering replaces");

        string root = Path.GetFullPath(cwd);
        SyncRoot fence = SyncRoot.Repo(root);
        StringBuilder builder = new(houseRules.Replace(ReadInstruction, InlineInstruction, StringComparison.Ordinal));
        foreach (string name in fileNames)
            builder.Append("\n\n").Append(Inline(name, Path.Combine(root, name), fence, warnings));

        return builder.ToString();
    }

    private static string Inline(string name, string path, SyncRoot fence, List<string> warnings)
    {
        if (!File.Exists(path))
            return $"### {name} (not present in the working directory)";

        if (fence.Escapes(path))
        {
            warnings.Add($"{name} not inlined: {path} resolves outside the working directory ({SyncRoot.Resolve(path)})");
            return $"### {name} (not inlined: it resolves outside the working directory)";
        }

        try
        {
            using FileStream? stream = PlainFile.OpenRead(path, out string? refusal);
            if (stream is null)
            {
                warnings.Add($"{name} not inlined: {refusal}");
                return $"### {name} (not inlined: it is not a regular file)";
            }

            // Open first, then prove where the handle lands: a link swapped in after the check above is what this catches.
            if (Unfit(stream, path, fence) is { } problem)
            {
                warnings.Add($"{name} not inlined: {problem}");
                return $"### {name} (not inlined: its real path is not a Markdown file inside the working directory)";
            }

            byte[] buffer = new byte[CapBytes + 1];
            int read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            string heading = $"### {name} (inlined; the api backend has no file access)";
            if (read <= CapBytes)
                return Section(heading, Decode(buffer, read));

            // Back off to the start of a character, so the cut never splits a multi-byte sequence.
            int cut = CapBytes;
            while (cut > 0 && (buffer[cut] & 0b1100_0000) == 0b1000_0000)
                cut--;

            warnings.Add($"{name} inlined truncated to {cut} of {stream.Length} bytes");
            return Section(heading, $"{Decode(buffer, cut)}\n\n[truncated: the first {cut} of {stream.Length} bytes of {name} are shown]");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"{name} not inlined: {e.Message}");
            return $"### {name} (not inlined: it could not be read)";
        }
    }

    // Why the opened file may not be inlined, or null. Inside the fence, a regular file named *.md, with no `.git`
    // component below the root (`AGENTS.md -> .git/config` carries a CI token): the same rules for a plain file and a link.
    private static string? Unfit(FileStream stream, string path, SyncRoot fence)
    {
        string? real = OpenedFilePath.TryGet(stream.SafeFileHandle, path);
        string? root = OpenedFilePath.RootSpelling(fence);
        if (real is null || root is null)
            return "the real path of the opened file could not be determined";

        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string trimmedRoot = Path.TrimEndingDirectorySeparator(root);
        if (!real.StartsWith(trimmedRoot + Path.DirectorySeparatorChar, comparison))
            return $"the opened file is {real}, outside the working directory";

        bool inGit = real[(trimmedRoot.Length + 1)..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(PlainFile.IsGitDirName);
        return stream.CanSeek && !inGit && real.EndsWith(".md", comparison)
            ? null
            : $"the opened file is {real}, which is not a regular .md file outside .git";
    }

    private static string Section(string heading, string content) =>
        content.Length == 0 ? $"{heading}\n\n(the file is empty)" : $"{heading}\n\n{content}";

    private static string Decode(byte[] buffer, int length) => Encoding.UTF8.GetString(buffer, 0, length).Trim('﻿').Trim();
}
