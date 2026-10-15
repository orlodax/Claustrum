using Claustrum.Core.IO;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests.IO;

// #124 stage 3 review F2: `answered.json` is claimed with TryCreate, so of several answers racing for one job exactly one wins. The trap measured
// 2026-10-15 is that .NET's File.Move(overwrite: false) is not exclusive on Linux (8 threads, several winners in 284 of 300 rounds).
public sealed class AtomicFileTryCreateTests : IDisposable
{
    private readonly ScratchRoot scratch = new("claustrum-trycreate-");

    public void Dispose() => scratch.Dispose();

    private string PathIn(string name) => Path.Combine(scratch.CreateDirectory(), name);

    [Fact]
    public void TheFirstWriterCreatesTheFileWholeAndLeavesNoTempFileBehind()
    {
        string path = PathIn("answered.json");

        bool created = AtomicFile.TryCreate(path, "first");

        Assert.True(created);
        Assert.Equal("first", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFileSystemEntries(Path.GetDirectoryName(path) ?? ""));
    }

    [Fact]
    public void ASecondWriterIsRefusedAndTheFirstTextSurvives()
    {
        string path = PathIn("answered.json");
        AtomicFile.TryCreate(path, "first");

        bool created = AtomicFile.TryCreate(path, "second");

        Assert.False(created);
        Assert.Equal("first", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFileSystemEntries(Path.GetDirectoryName(path) ?? ""));
    }

    [Fact]
    public async Task ManyWritersRacingForOneNameProduceExactlyOneWinnerEveryRoundAsync()
    {
        // The measured failure rate of a plain move was 284 rounds in 300, so a handful of rounds is enough to catch a regression to it.
        for (int round = 0; round < 40; round++)
        {
            string path = PathIn($"claim-{round}.json");
            using ManualResetEventSlim go = new();
            Task<bool>[] writers = [.. Enumerable.Range(0, 8).Select(writer => Task.Run(() =>
            {
                go.Wait();
                return AtomicFile.TryCreate(path, $"writer {writer}");
            }, CancellationToken.None))];

            go.Set();
            bool[] outcomes = await Task.WhenAll(writers);

            Assert.Equal(1, outcomes.Count(won => won));
            Assert.StartsWith("writer ", File.ReadAllText(path), StringComparison.Ordinal);
        }
    }
}
