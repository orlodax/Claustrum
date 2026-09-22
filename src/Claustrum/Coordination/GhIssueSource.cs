using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Claustrum.Cli;
using Claustrum.Core.Platform;
using Claustrum.Core.Process;

namespace Claustrum.Coordination;

// docs/PLAN.md §D3: "`gh issue view <n> --json title,body,labels` becomes the brief's `## Task`;
// `gh` presence is a `doctor` check". Everything that can go wrong here is the user's environment
// (no gh, not logged in, no such issue, not a GitHub remote), so every failure is a CliUsageException
// — exit 2, not a backend failure. Runs through CommandProcess for the stdin/timeout fixes.
public sealed class GhIssueSource(IPlatform platform) : IIssueSource
{
    // gh talks to github.com, so it is slower than a local `git` call and still must not hang a
    // coordinate that is about to spend money on an architect.
    private static readonly TimeSpan timeout = TimeSpan.FromSeconds(60);

    public async Task<GhIssue> ViewAsync(string cwd, int number, CancellationToken cancellationToken)
    {
        string[] args = ["issue", "view", number.ToString(CultureInfo.InvariantCulture), "--json", "number,title,body,labels,url"];

        // The same resolution `backends doctor` probes gh with (VersionProbe → BinaryLocator: PATH +
        // PATHEXT + the Windows npm-shim unwrap). A bare "gh" handed to Process.Start only ever gains
        // `.exe`, so a gh the doctor reports as found could still fail to start here.
        ResolvedBinary binary;
        try
        {
            binary = BinaryLocator.Locate("gh", args, config: null, platform);
        }
        catch (BackendNotFoundException)
        {
            throw Failure(number, "'gh' was not found on PATH — run `claustrum backends doctor` to check gh");
        }

        int exitCode;
        string stdout;
        string stderr;
        try
        {
            (exitCode, stdout, stderr) = await CommandProcess.RunAsync(binary.Executable, cwd, binary.Args, timeout, cancellationToken);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or TimeoutException)
        {
            // Win32Exception is what a missing binary looks like from Process.Start on both OSes.
            throw Failure(number, $"{ex.Message} — run `claustrum backends doctor` to check gh");
        }

        if (exitCode != 0)
            throw Failure(number, stderr.Trim());

        GhIssueDocument document;
        try
        {
            document = JsonSerializer.Deserialize(stdout, CoordinationJsonContext.Default.GhIssueDocument)
                ?? throw Failure(number, "gh returned no JSON object");
        }
        catch (JsonException ex)
        {
            throw Failure(number, ex.Message);
        }

        return new GhIssue(
            document.Number > 0 ? document.Number : number,
            document.Title ?? "",
            document.Body ?? "",
            [.. (document.Labels ?? []).Select(label => label.Name).OfType<string>()],
            document.Url ?? "");
    }

    // One message shape for every way gh can fail, without repeating the prefix at every throw site.
    private static CliUsageException Failure(int number, string detail) =>
        new($"gh issue view {number} failed: {detail}");
}
