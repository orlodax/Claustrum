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
    /// outcome in <paramref name="into"/>.
    /// </summary>
    public void Write(string path, string role, string frontmatter, string body, bool force, SyncMode mode, SyncAccumulator into)
    {
        string trimmedBody = body.Trim();
        string sha256 = ComputeSha256(trimmedBody);
        string marker = $"{MarkerPrefix} role={role} harness={harness} library={libraryVersion} sha256={sha256} -->";
        string content = $"{frontmatter.Trim()}\n{marker}\n\n{trimmedBody}\n";

        if (File.Exists(path))
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
            File.WriteAllText(path, content);
        else
            into.ProposedContent[path] = content;

        into.Written.Add(path);
        into.ManifestFiles.Add(new SyncManifestFile(path, role, harness, sha256));
    }

    /// <summary>The `-xhigh`/`-max` stub descriptions, identical across every harness.</summary>
    public static string TierDescription(string role, string tier)
    {
        string capitalized = char.ToUpperInvariant(role[0]) + role[1..];
        return tier switch
        {
            "xhigh" => $"{capitalized} at EXTRA (xhigh) reasoning effort — identical role, model, and rules as the "
                + $"`{role}` agent, but thinks harder. Routine work → `{role}`; the hardest cases → `{role}-max`.",
            "max" => $"{capitalized} at MAX reasoning effort — identical role, model, and rules as the `{role}` "
                + "agent, with the deepest reasoning and no token-spend constraint. Reserve for genuinely hard, "
                + $"high-stakes, or previously-stuck cases. For everyday work use `{role}`; for a step up use `{role}-xhigh`.",
            _ => throw new RoleRenderException($"no stub description template for tier '{tier}'"),
        };
    }

    private static string NormalizeLineEndings(string text) => text.Replace("\r\n", "\n");

    private static bool HasMarker(string content) =>
        content.Split('\n').Take(15).Any(line => line.TrimEnd('\r').StartsWith(MarkerPrefix, StringComparison.Ordinal));

    private static string ComputeSha256(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
