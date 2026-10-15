using Claustrum.Core.IO;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests.IO;

// #124 stage 2 review F5: host.json, host-request.json and owner.json are written temp-file-then-rename, so a reader polling the job
// directory never sees one cut short. The pattern is asserted through the file system: the text goes to `<file>.tmp` first and
// the target changes only by the rename.
public sealed class AtomicFileTests : IDisposable
{
    private readonly ScratchRoot scratch = new("claustrum-atomicfile-");

    public void Dispose() => scratch.Dispose();

    private string PathIn(string name) => Path.Combine(scratch.CreateDirectory(), name);

    [Fact]
    public void WritesTheTextAndLeavesNoTempFileBehind()
    {
        string path = PathIn("host.json");

        AtomicFile.WriteAllText(path, "pid 1");

        Assert.Equal("pid 1", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFileSystemEntries(Path.GetDirectoryName(path) ?? ""));
    }

    [Fact]
    public void ReplacesAnExistingFileWhole()
    {
        string path = PathIn("owner.json");
        File.WriteAllText(path, new string('x', 5000));

        AtomicFile.WriteAllText(path, "short");

        Assert.Equal("short", File.ReadAllText(path));
    }

    // The text is written to `<file>.tmp` before anything touches the target: with the temp path unusable the write fails and the
    // old content of the target is still all there. A write straight to the target would have truncated it first.
    [Fact]
    public void TheTextGoesToATempFileFirstSoAFailedWriteLeavesTheTargetIntact()
    {
        string path = PathIn("host-request.json");
        File.WriteAllText(path, "the old content, whole");
        Directory.CreateDirectory(path + ".tmp");

        Assert.ThrowsAny<Exception>(() => AtomicFile.WriteAllText(path, "the new content"));

        Assert.Equal("the old content, whole", File.ReadAllText(path));
    }

    // Black-box: two payloads of very different sizes written over each other while a reader reads the target in a loop. Every read is
    // one payload whole; a truncate-and-write would be caught empty or half-written.
    [Fact]
    public async Task AReaderOnlyEverSeesAWholePayloadWhileTheFileIsRewrittenAsync()
    {
        string path = PathIn("host.json");
        string small = new('a', 10);
        string large = new('b', 4_000_000);
        AtomicFile.WriteAllText(path, small);
        using CancellationTokenSource stop = new();
        List<int> badLengths = [];
        Task reader = Task.Run(
            () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    try
                    {
                        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using StreamReader text = new(stream);
                        string content = text.ReadToEnd();
                        if (content != small && content != large)
                            badLengths.Add(content.Length);
                    }
                    catch (IOException)
                    {
                        // Replaced between the open and the read on a platform that refuses it: not a torn file.
                    }
                }
            },
            CancellationToken.None);

        for (int i = 0; i < 40; i++)
            AtomicFile.WriteAllText(path, i % 2 == 0 ? large : small);

        await stop.CancelAsync();
        await reader;
        Assert.Empty(badLengths);
    }
}
