using System.Text.Json;
using Claustrum.Cli;
using Claustrum.Coordination;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// #124 stage 2 review F1, the settle end: one real `coordinate` through the built binary, whose fake architect starts detached children of
// its tree. DetachedRepo kills any host a failure leaves.
public sealed class CoordinateSettleEndToEndTests : IDisposable
{
    private static readonly TimeSpan cancelWithin = TimeSpan.FromSeconds(20);

    private readonly DetachedRepo fixture = new(forCoordinate: true);

    public void Dispose() => fixture.Dispose();

    // One real `coordinate`, whose fake architect starts two detached children of its tree and cancels the first itself, then ends. The
    // second is still waiting, so coordinate's settle cancels it and lists it, and only it, in the receipt it returns and the one on disk.
    [Fact]
    public async Task CoordinateSettleCancelsItsRemainingDetachedChildrenAndListsThemInBothReceiptsAsync()
    {
        string bin = Path.Combine(AppContext.BaseDirectory, "claustrum");
        string fakes = Path.GetDirectoryName(fixture.Repo.ScriptPath) ?? "";
        string builder = Path.Combine(fakes, "builder-claude.sh");
        string architect = Path.Combine(fakes, "architect-claude.sh");
        string ids = Path.Combine(fakes, "ids.txt");
        FakeClaude.Write(builder, new FakeClaudeScript { Turns = [FakeClaudeTurn.Ask("hold on")] });
        FakeClaude.Write(architect, new FakeClaudeScript
        {
            Commands =
            [
                $"export HOME='{fixture.Repo.Home}' CLAUSTRUM_HOME='{fixture.Repo.Home}' CLAUSTRUM_SKIP_PROBE=1 PATH='{IsolatedRepo.ChildPath()}'",
                $"A=$('{bin}' run builder --brief a --detach --reply-timeout 600) || exit 3",
                $"B=$('{bin}' run builder --brief b --detach --reply-timeout 600) || exit 3",
                $"printf '%s\\n' \"$A\" \"$B\" > '{ids}'",
                $"'{bin}' jobs cancel \"$A\" > /dev/null 2>&1",
            ],
            Summary = "started two children",
        });

        // The member's environment lacks the tree id (a member never forwards it), so it is the builder, while the architect has it.
        File.WriteAllText(
            fixture.Repo.ScriptPath,
            $"#!/bin/sh\nif [ -n \"$CLAUSTRUM_PARENT_JOB\" ]; then exec '{architect}' \"$@\"; fi\nexec '{builder}' \"$@\"\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(fixture.Repo.ScriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using CancellationTokenSource stop = new();
        Task<string[]> watcher = fixture.WatchForTornFilesAsync(stop.Token, "owner.json", "host.json", "host-request.json");

        (CliResult process, JsonElement? parsed) = await fixture.Repo.CoordinateAsync();
        await stop.CancelAsync();

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr + process.Stdout);
        JsonElement result = parsed ?? throw new InvalidOperationException(process.Stdout);
        string[] childIds = File.ReadAllLines(ids);
        string cancelledByArchitect = childIds[0];
        string remaining = childIds[1];
        string coordinate = result.GetProperty("job_id").GetString() ?? "";
        string expected = TreeSettler.WarningFor(remaining);

        string[] returned = [.. result.GetProperty("warnings").EnumerateArray().Select(warning => warning.GetString() ?? "")];
        string[] onDisk = [.. fixture.Json(coordinate, "result.json")?.GetProperty("warnings").EnumerateArray().Select(warning => warning.GetString() ?? "") ?? []];
        Assert.Contains(expected, returned);
        Assert.Contains(expected, onDisk);
        Assert.DoesNotContain(returned, warning => warning.Contains(cancelledByArchitect, StringComparison.Ordinal));
        Assert.DoesNotContain(onDisk, warning => warning.Contains(cancelledByArchitect, StringComparison.Ordinal));

        // `jobs show` reads the disk, and shows the same warning.
        CliResult show = await fixture.Repo.RunAsync("jobs", "show", coordinate);
        Assert.Contains(expected, show.Stdout, StringComparison.Ordinal);

        await fixture.WaitForResultAsync(remaining, cancelWithin);
        Assert.Equal("cancelled", fixture.ResultStatus(remaining));
        Assert.Equal("cancelled", fixture.ResultStatus(cancelledByArchitect));
        await fixture.WaitForHostGoneAsync(remaining);
        Assert.True(fixture.Host(remaining)?.Tree == coordinate, "the host recorded the coordinate job as its tree");
        Assert.True(File.Exists(Path.Combine(fixture.JobDir(coordinate), "owner.json")));
        Assert.Empty(await watcher);
        Assert.Empty(Directory.GetFiles(fixture.JobDir(coordinate), "*.tmp"));
    }
}
