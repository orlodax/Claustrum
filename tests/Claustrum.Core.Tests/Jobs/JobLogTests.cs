using System.Text;
using Claustrum.Core.Jobs;

namespace Claustrum.Core.Tests.Jobs;

// JobLog reads a log file that ProcessRunner's StreamWriter(path, append: false) may still hold
// open for writing (NOTES.md "Reading a live job log needs FileShare.ReadWrite"): the reader has to
// open with FileShare.ReadWrite, not the .NET default FileShare.Read, or Windows throws "the process
// cannot access the file" (PR #26 windows leg). On Linux the default share mode already allows a
// concurrent reader, so the write-held cases here pass trivially there — they exist to pin the
// Windows-safe behaviour and to catch a regression to File.ReadAllLinesAsync/File.ReadAllText.
public sealed class JobLogTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("claustrum-joblog-").FullName;

    public void Dispose() => Directory.Delete(directory, recursive: true);

    [Fact]
    public void LastLineReadsThroughAWriterStillHoldingTheFileOpenForWrite()
    {
        string path = Path.Combine(directory, "stdout.log");
        using StreamWriter writer = new(path, append: false) { AutoFlush = true };
        writer.WriteLine("a");
        writer.WriteLine("b");

        Assert.Equal("b", JobLog.LastLine(path));
    }

    [Fact]
    public void ReadLinesYieldsBothLinesWhileTheWriterIsStillOpen()
    {
        string path = Path.Combine(directory, "stdout.log");
        using StreamWriter writer = new(path, append: false) { AutoFlush = true };
        writer.WriteLine("a");
        writer.WriteLine("b");

        Assert.Equal(["a", "b"], JobLog.ReadLines(path));
    }

    [Fact]
    public void ReadAllTextReturnsTheTextWhileTheWriterIsStillOpen()
    {
        string path = Path.Combine(directory, "stdout.log");
        using StreamWriter writer = new(path, append: false) { AutoFlush = true };
        writer.WriteLine("a");
        writer.WriteLine("b");

        Assert.Equal($"a{Environment.NewLine}b{Environment.NewLine}", JobLog.ReadAllText(path));
    }

    [Fact]
    public void LastLineOnAMissingPathIsNull() =>
        Assert.Null(JobLog.LastLine(Path.Combine(directory, "never-written.log")));

    [Fact]
    public async Task ReadAllTextStripsAUtf8BomInsteadOfReturningItAsATextCharAsync()
    {
        string path = Path.Combine(directory, "bom.log");
        await File.WriteAllTextAsync(path, "first line", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), TestContext.Current.CancellationToken);

        string text = JobLog.ReadAllText(path);

        Assert.Equal("first line", text);
        Assert.DoesNotContain('﻿', text);
    }

    // File.Delete on Windows fails if any FileStream from a previous ReadLines call is still open —
    // proof that the iterator's `using` disposes the handle even when the caller only pulls the
    // first element and never finishes enumerating. Passes unconditionally on Linux too.
    [Fact]
    public void PartiallyEnumeratedReadLinesDoesNotLeakTheFileHandle()
    {
        string path = Path.Combine(directory, "partial.log");
        File.WriteAllLines(path, ["a", "b", "c"]);

        string first = JobLog.ReadLines(path).First();

        Assert.Equal("a", first);
        File.Delete(path);
        Assert.False(File.Exists(path));
    }
}
