using System.Text;
using Claustrum.Core.IO;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests.IO;

// PlainFile (2026-10-13): a FIFO or a Unix socket standing where a file is expected is refused, never opened for a read that
// blocks until a writer appears. The pipe cases are Unix only (mkfifo; a socket node); PipeFiles.Within turns a regression
// into a failed test instead of a run that never ends.
public sealed class PlainFileTests : IDisposable
{
    private readonly ScratchRoot scratch = new("claustrum-plainfile-");

    public void Dispose() => scratch.Dispose();

    private string PathIn(string name) => Path.Combine(scratch.CreateDirectory(), name);

    [Fact]
    public void ARegularFileOpensAndReadsItsBytes()
    {
        string path = PathIn("a.txt");
        File.WriteAllText(path, "hello");

        using FileStream? stream = PlainFile.OpenRead(path, out string? refusal);

        Assert.NotNull(stream);
        Assert.Null(refusal);
        Assert.Equal("hello", new StreamReader(stream).ReadToEnd());
    }

    [Fact]
    public void TryReadAllTextReadsARegularFileAndStripsTheByteOrderMark()
    {
        string path = PathIn("bom.txt");
        File.WriteAllBytes(path, [.. Encoding.UTF8.GetPreamble(), .. "café"u8.ToArray()]);

        bool ok = PlainFile.TryReadAllText(path, out string? text, out string? refusal);

        Assert.True(ok);
        Assert.Null(refusal);
        Assert.Equal("café", text);
    }

    [Fact]
    public void AMissingFileThrowsFileNotFound()
    {
        Assert.Throws<FileNotFoundException>(() => PlainFile.OpenRead(PathIn("not-there"), out _));
        Assert.Throws<FileNotFoundException>(() => PlainFile.TryReadAllText(PathIn("not-there"), out _, out _));
    }

    [Fact]
    public void ADirectoryIsAnErrorNotARefusal()
    {
        string directory = scratch.CreateDirectory();

        Exception ex = Assert.ThrowsAny<Exception>(() => PlainFile.TryReadAllText(directory, out _, out _));

        Assert.True(ex is UnauthorizedAccessException or IOException, ex.GetType().Name);
    }

    [Fact]
    public void AFifoIsRefusedAsNotARegularFileWithoutBlocking()
    {
        string path = PathIn("pipe");
        PipeFiles.MakeFifo(path);

        (FileStream? stream, string? refusal) = PipeFiles.Within(() => (PlainFile.OpenRead(path, out string? why), why));

        Assert.Null(stream);
        Assert.Contains("not a regular file", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void TryReadAllTextRefusesAFifoWithoutBlocking()
    {
        string path = PathIn("pipe");
        PipeFiles.MakeFifo(path);

        (bool ok, string? text, string? refusal) = PipeFiles.Within(() => (PlainFile.TryReadAllText(path, out string? t, out string? why), t, why));

        Assert.False(ok);
        Assert.Null(text);
        Assert.Contains("not a regular file", refusal, StringComparison.Ordinal);
    }

    // open(2) on a socket node fails with ENXIO; that is a refusal, not an IOException.
    [Fact]
    public void ABoundUnixSocketIsRefusedLikeAPipeNotThrownAsAnIoError()
    {
        string path = PathIn("sock");
        using System.Net.Sockets.Socket socket = PipeFiles.BindSocket(path);

        (FileStream? stream, string? refusal) = PipeFiles.Within(() => (PlainFile.OpenRead(path, out string? why), why));

        Assert.Null(stream);
        Assert.Contains("not a regular file", refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(".git")]
    [InlineData(".GIT")]
    [InlineData(".Git")]
    public void TheGitDirectoryIsRecognisedInAnyCaseOnEveryOs(string segment)
    {
        Assert.True(PlainFile.IsGitDirName(segment));
    }

    [Theory]
    [InlineData(".github")]
    [InlineData("git")]
    [InlineData(".gitx")]
    [InlineData("GIT~")]
    [InlineData("")]
    [InlineData("x.git")]
    public void ANameThatOnlyLooksLikeGitIsNotTheGitDirectory(string segment)
    {
        Assert.False(PlainFile.IsGitDirName(segment));
    }

    [Theory]
    [InlineData(".git.")]
    [InlineData(".git ")]
    [InlineData(".git. .")]
    [InlineData("GIT~1")]
    [InlineData("git~12")]
    [InlineData(".git::$INDEX_ALLOCATION")]
    [InlineData(".git:foo")]
    [InlineData(".git.:x")]
    [InlineData("GIT~1::$I30")]
    public void WindowsAlternateSpellingsOfTheGitDirectoryAreRecognised(string segment)
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("trailing dots and spaces, 8.3 short names and NTFS stream suffixes are Windows spellings.");

        Assert.True(PlainFile.IsGitDirName(segment));
    }

    [Theory]
    [InlineData("GIT~1x")]
    [InlineData(":x")]
    [InlineData("gitx::$DATA")]
    public void WindowsNamesThatAreNotTheGitDirectoryStayUnrecognised(string segment)
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("the Windows spellings are only interpreted on Windows.");

        Assert.False(PlainFile.IsGitDirName(segment));
    }

    // Off Windows `.git.` is an ordinary file name (a repo may hold one), and so are `GIT~1` and a stream-looking name.
    [Theory]
    [InlineData(".git.")]
    [InlineData("GIT~1")]
    [InlineData(".git::$X")]
    public void OffWindowsTheWindowsSpellingsAreOrdinaryNames(string segment)
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("on Windows these are the .git directory.");

        Assert.False(PlainFile.IsGitDirName(segment));
    }
}
