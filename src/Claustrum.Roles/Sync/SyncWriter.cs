using System.Security.Cryptography;
using System.Text;
using Claustrum.Roles.Model;

namespace Claustrum.Roles.Sync;

/// <summary>
/// The marker/idempotency machinery every harness's Sync needs: stamp a generated file with
/// `claustrum:generated` + the body's sha256, leave a byte-identical file alone, and refuse to
/// overwrite a hand-written one without <c>force</c>.
/// </summary>
/// <remarks>
/// OpencodeSync's original doc comment deferred this extraction until "Cursor/CopilotSync exist too
/// and the real common shape is known, not guessed from two data points". CopilotSync made three
/// verbatim copies, so the condition is met (review finding): the only per-harness differences turned
/// out to be the harness name, where files live, and how frontmatter is built — everything below is
/// shared. TierDescription moved here for the same reason, and unified on ClaudeSync's `→`; the
/// two later copies had retyped it as `-&gt;`, which no test or golden pinned either way.
/// </remarks>
public sealed class SyncWriter(string harness, string libraryVersion)
{
    public const string MarkerPrefix = "<!-- claustrum:generated";

    /// <summary>
    /// Writes (or, outside <see cref="SyncMode.Write"/>, proposes) one generated file and records the
    /// outcome in <paramref name="into"/>. <paramref name="localOverride"/> stamps `source=local`
    /// into the marker: the file was rendered from `.claustrum/roles/`, not the library alone.
    /// A file whose landing directory resolves outside a fenced <paramref name="root"/> is refused (#95).
    /// The real path recorded in <paramref name="into"/> is where the bytes land: the parent resolved
    /// plus the leaf's name, so a replaced leaf link reports its landing path, never the link's target.
    /// </summary>
    public void Write(string path, string role, string frontmatter, string body, bool force, SyncMode mode, SyncAccumulator into, SyncRoot root, bool localOverride = false)
    {
        string trimmedBody = body.Trim();
        string sha256 = ComputeSha256(trimmedBody);
        string source = localOverride ? " source=local" : "";
        string marker = $"{MarkerPrefix} role={role} harness={harness} library={libraryVersion}{source} sha256={sha256} -->";
        string content = $"{frontmatter.Trim()}\n{marker}\n\n{trimmedBody}\n";

        // The bytes land in the leaf's parent (a leaf link is replaced, never written through, #84), so
        // the recorded real path is that directory resolved plus the leaf's own name, not the link's target.
        string parent = Path.GetDirectoryName(path) ?? path;
        string real = Path.Combine(SyncRoot.Resolve(parent), Path.GetFileName(path));
        if (!SyncRoot.SamePath(path, real))
            into.RealPaths[path] = real;

        // That directory, not the leaf, is what must stay inside the root (#95).
        if (root.Escapes(parent))
        {
            into.Refused[path] = real;
            return;
        }

        // A link is never sync's own file (sync never creates one), so it is foreign whatever it points
        // at, even a target carrying our marker, and never "up to date". LinkTarget goes first because
        // File.Exists does not tell a dangling link from a file and ReadAllText through one throws (#84).
        string? linkTarget = new FileInfo(path).LinkTarget;
        if (linkTarget is not null)
        {
            into.Symlinks[path] = linkTarget;
            if (!force)
            {
                into.Foreign.Add(path);
                return;
            }
        }
        else if (File.Exists(path))
        {
            string existing = File.ReadAllText(path);

            // Compare with line endings normalized: a CRLF checkout (core.autocrlf) makes
            // File.ReadAllText return `\r\n` while `content` above is built with plain `\n`, so a
            // byte-exact compare here rewrote every generated file on every `sync` (review finding #3).
            if (NormalizeLineEndings(existing) == NormalizeLineEndings(content))
            {
                into.Skipped.Add(path);
                into.ManifestFiles.Add(new SyncManifestFile(path, role, harness, sha256));
                return;
            }

            if (!HasMarker(existing) && !force)
            {
                into.Foreign.Add(path);
                return;
            }
        }

        // SyncMode.DryRun/Check never touch disk: the content that would have been written is kept
        // for the CLI to diff instead (SyncResult.ProposedContent).
        if (mode == SyncMode.Write)
        {
            if (linkTarget is null)
            {
                // In place keeps the file's mode and works in a read-only directory; the marker (or
                // --force) already proves the file is ours. A hard link is therefore written through:
                // LinkTarget cannot see one, and that is the accepted limit (NOTES.md, issue #84).
                File.WriteAllText(path, content);
            }
            else
                ReplaceLink(path, content);
        }
        else
            into.ProposedContent[path] = content;

        into.Written.Add(path);
        into.ManifestFiles.Add(new SyncManifestFile(path, role, harness, sha256));
    }

    // Unquoted, a description containing ": " is not a valid plain YAML scalar: copilot 1.0.87 threw
    // "mapping values are not allowed in this context" and silently skipped architect, code-reviewer
    // and tester (2026-09-22, measured — NOTES.md "The copilot backend, validated against a real
    // install"). Claude/opencode/cursor emitted the identical unquoted line and are fixed the same way
    // (issue #33) — only the two characters a double-quoted YAML scalar reserves need escaping.
    public static string YamlQuoted(string value) =>
        $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    /// <summary>
    /// The `-xhigh`/`-max` stub descriptions, identical across every harness. <paramref name="sameModel"/>
    /// is whether the model the harness file names for the tier equals the base agent's: only then is
    /// "identical model" true; otherwise the stub says it is a model+effort step up (#30).
    /// </summary>
    public static string TierDescription(string role, string tier, bool sameModel)
    {
        string capitalized = char.ToUpperInvariant(role[0]) + role[1..];
        string identical = sameModel ? "identical role, model, and rules" : "identical role and rules";
        return tier switch
        {
            "xhigh" => $"{capitalized} at EXTRA (xhigh) reasoning effort — {identical} as the "
                + $"`{role}` agent, {(sameModel ? "but thinks harder" : "a model+effort step up")}. Routine work → `{role}`; the hardest cases → `{role}-max`.",
            "max" => $"{capitalized} at MAX reasoning effort — {identical} as the `{role}` "
                + $"agent, {(sameModel ? "with" : "a model+effort step up with")} the deepest reasoning and no token-spend constraint. Reserve for genuinely hard, "
                + $"high-stakes, or previously-stuck cases. For everyday work use `{role}`; for a step up use `{role}-xhigh`.",
            _ => throw new RoleRenderException($"no stub description template for tier '{tier}'"),
        };
    }

    // The temp name is random and opened with CreateNew (O_EXCL), which also refuses a planted
    // symlink: a committed link at a guessable temp name would be written through (found 2026-10-12).
    private static void ReplaceLink(string path, string content)
    {
        string tmp = Path.Combine(Path.GetDirectoryName(path) ?? "", $".{Path.GetFileName(path)}.{Path.GetRandomFileName()}.claustrum-tmp");
        try
        {
            using (StreamWriter writer = new(new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None)))
                writer.Write(content);

            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            if (File.Exists(tmp))
                File.Delete(tmp);
            throw;
        }
    }

    private static string NormalizeLineEndings(string text) => text.Replace("\r\n", "\n");

    private static bool HasMarker(string content) =>
        content.Split('\n').Take(15).Any(line => line.TrimEnd('\r').StartsWith(MarkerPrefix, StringComparison.Ordinal));

    private static string ComputeSha256(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
