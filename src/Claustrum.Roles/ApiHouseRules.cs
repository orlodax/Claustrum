using System.Text;
using Claustrum.Core.IO;
using Claustrum.Roles.Sync;

namespace Claustrum.Roles;

// #55: the api backend has no file access, so "read AGENTS.md" cannot be obeyed there; the files ride in the
// system prompt instead. The content goes to a third-party API, hence the fence: a file whose real path leaves
// the cwd is never read. Unix opens by a no-follow walk from `/` (no link can be what is read), Windows judges the
// OPENED handle; hard-linked files are refused (NOTES.md "The api backend gets AGENTS.md and CLAUDE.md inlined").
internal static class ApiHouseRules
{
    public const int CapBytes = 32 * 1024;

    private const string ReadInstruction = "Read `AGENTS.md` and `CLAUDE.md` in the working directory first; they are law";
    private const string InlineInstruction = "The repository's rules follow; they are law";

    private static readonly string[] fileNames = ["AGENTS.md", "CLAUDE.md"];

    /// <summary>
    /// <paramref name="houseRules"/> with its read instruction replaced and the cwd's rule files appended under
    /// their own headings; what could not be inlined as it should is named in <paramref name="warnings"/>. An isolated
    /// run passes the real path its worktree must have as <paramref name="expectedRoot"/>: anything else inlines nothing,
    /// and the walk is rooted there, not at wherever <paramref name="cwd"/> resolves now.
    /// </summary>
    public static string Compose(string houseRules, string cwd, List<string> warnings, string? expectedRoot = null)
    {
        if (!houseRules.Contains(ReadInstruction, StringComparison.Ordinal))
            throw new RoleRenderException("_shared/house-rules.md no longer carries the read instruction the api rendering replaces");

        string root = Path.GetFullPath(cwd);
        SyncRoot fence = SyncRoot.Repo(root);
        StringBuilder builder = new(houseRules.Replace(ReadInstruction, InlineInstruction, StringComparison.Ordinal));
        if (expectedRoot is not null && !Anchored(root, expectedRoot, out string resolved))
        {
            warnings.Add($"rule files not inlined: the worktree {root} resolves to {resolved}, outside the repository's .claustrum/worktrees ({expectedRoot})");
            foreach (string name in fileNames)
                builder.Append("\n\n### ").Append(name).Append(" (not inlined: the worktree resolves outside the repository's .claustrum/worktrees)");

            return builder.ToString();
        }

        string realRoot = expectedRoot ?? fence.RealPath;
        foreach (string name in fileNames)
            builder.Append("\n\n").Append(Inline(name, Path.Combine(root, name), fence, realRoot, warnings));

        return builder.ToString();
    }

    // The root a run was promised (#55, review 2026-10-14): a link at or above the worktree is what an attacker inside
    // the cwd would plant, and the fence built from the typed path would then be the outside directory itself.
    private static bool Anchored(string root, string expected, out string resolved)
    {
        resolved = SyncRoot.Resolve(root, out bool unresolved);
        return !unresolved && resolved.Equals(expected, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static string Inline(string name, string path, SyncRoot fence, string realRoot, List<string> warnings)
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
            using FileStream? stream = Open(path, fence, realRoot, out string? refusal);
            if (stream is null)
            {
                warnings.Add($"{name} not inlined: {refusal}");
                return $"### {name} (not inlined: {Reason(refusal)})";
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

    // The file as a handle that is proven fit to inline, or null with the reason. Unix: the fully resolved path is judged
    // as a string, then opened by a no-follow walk from `/` through realRoot, so no link can be what is read and nothing
    // is compared afterwards. Windows: opened first, then the handle's own final path is judged (GetFinalPathNameByHandle).
    private static FileStream? Open(string path, SyncRoot fence, string realRoot, out string? refusal)
    {
        if (OperatingSystem.IsWindows())
            return OpenAndJudge(path, fence, out refusal);

        string real = SyncRoot.Resolve(path, out bool unresolved);
        if (unresolved)
        {
            refusal = $"{PlainFile.Unverifiable} (the real path of the file could not be determined)";
            return null;
        }

        string root = Path.TrimEndingDirectorySeparator(realRoot);
        if (Misfit(real, root) is { } problem)
        {
            refusal = problem;
            return null;
        }

        return PlainFile.OpenNoFollowUnder(root.Length == 0 ? "/" : root, real[(root.Length + 1)..].Split(Path.DirectorySeparatorChar), out refusal);
    }

    private static FileStream? OpenAndJudge(string path, SyncRoot fence, out string? refusal)
    {
        FileStream? stream = PlainFile.OpenRead(path, out refusal);
        if (stream is null)
            return null;

        string? real = OpenedFilePath.TryGet(stream.SafeFileHandle);
        string? root = OpenedFilePath.RootSpelling(fence);
        refusal = real is null || root is null
            ? $"{PlainFile.Unverifiable} (the real path of the opened file could not be determined)"
            : Misfit(real, Path.TrimEndingDirectorySeparator(root));
        if (refusal is null && !stream.CanSeek)
            refusal = $"the opened file is {real}, which is not a regular file";

        refusal ??= OpenedFilePath.HasOtherHardLinks(stream.SafeFileHandle) switch
        {
            true => PlainFile.HardLinked,
            null => $"{PlainFile.Unverifiable} (the hard-link count could not be read)",
            false => null,
        };

        if (refusal is null)
            return stream;

        stream.Dispose();
        return null;
    }

    // Why a resolved path may not be inlined, or null: inside the root, named *.md, with no `.git` component below the
    // root (`AGENTS.md -> .git/config` carries a CI token). The same rules for a plain file and a link.
    private static string? Misfit(string real, string root)
    {
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!real.StartsWith(root + Path.DirectorySeparatorChar, comparison))
            return $"the file is {real}, outside the working directory";

        bool inGit = real[(root.Length + 1)..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(PlainFile.IsGitDirName);
        return !inGit && real.EndsWith(".md", comparison) ? null : $"the file is {real}, which is not a regular .md file outside .git";
    }

    private static string Reason(string? refusal) => refusal switch
    {
        PlainFile.NotRegular => "it is not a regular file",
        PlainFile.HardLinked => "it has other hard links",
        not null when refusal.StartsWith(PlainFile.Unverifiable, StringComparison.Ordinal) => PlainFile.Unverifiable,
        _ => "its real path is not a Markdown file inside the working directory",
    };

    private static string Section(string heading, string content) =>
        content.Length == 0 ? $"{heading}\n\n(the file is empty)" : $"{heading}\n\n{content}";

    private static string Decode(byte[] buffer, int length) => Encoding.UTF8.GetString(buffer, 0, length).Trim('﻿').Trim();
}
