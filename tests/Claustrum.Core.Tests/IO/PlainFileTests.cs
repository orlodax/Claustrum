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

    // The opened handle of a directory is not a regular file: refused on Unix; Windows cannot open one and throws.
    [Fact]
    public void ADirectoryIsNeverReadAsText()
    {
        string directory = scratch.CreateDirectory();

        if (OperatingSystem.IsWindows())
        {
            Exception ex = Assert.ThrowsAny<Exception>(() => PlainFile.TryReadAllText(directory, out _, out _));
            Assert.True(ex is UnauthorizedAccessException or IOException, ex.GetType().Name);
            return;
        }

        Assert.False(PlainFile.TryReadAllText(directory, out string? text, out string? refusal));
        Assert.Null(text);
        Assert.Contains("not a regular file", refusal, StringComparison.Ordinal);
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

    // ---- devices, the size cap, and the write door (2026-10-14) -----------------------------------------------------

    [Fact]
    public void ALinkToDevZeroIsRefusedOnTheOpenedHandleNotReadForeverOrIntoMemory()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("/dev/zero is a Unix device.");

        string link = PathIn("zero");
        File.CreateSymbolicLink(link, "/dev/zero");

        (bool ok, string? refusal) = PipeFiles.Within(() => (PlainFile.TryReadAllText(link, out _, out string? why), why));
        (FileStream? stream, string? openRefusal) = PipeFiles.Within(() => (PlainFile.OpenRead(link, out string? why), why));

        Assert.False(ok);
        Assert.Contains("not a regular file", refusal, StringComparison.Ordinal);
        Assert.Null(stream);
        Assert.Contains("not a regular file", openRefusal, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyFileIsAcceptedAsEmptyText()
    {
        string path = PathIn("empty.txt");
        File.WriteAllBytes(path, []);

        Assert.True(PlainFile.TryReadAllText(path, out string? text, out _));
        Assert.Equal("", text);
    }

    private static byte[] Filled(int length) => [.. Enumerable.Repeat((byte)'a', length)];

    [Fact]
    public void AFileOfExactlyEightMiBIsAcceptedAndOneByteMoreIsTooLarge()
    {
        string exact = PathIn("exact.txt");
        string over = PathIn("over.txt");
        File.WriteAllBytes(exact, Filled(PlainFile.MaxTextBytes));
        File.WriteAllBytes(over, Filled(PlainFile.MaxTextBytes + 1));

        Assert.True(PlainFile.TryReadAllText(exact, out string? text, out _));
        Assert.Equal(PlainFile.MaxTextBytes, text.Length);
        Assert.False(PlainFile.TryReadAllText(over, out string? none, out string? refusal));
        Assert.Null(none);
        Assert.Contains("too large", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void TryWriteAllTextCreatesAMissingFile()
    {
        string path = PathIn("new.txt");

        Assert.True(PlainFile.TryWriteAllText(path, "hello", FileMode.Create, out string? refusal));

        Assert.Null(refusal);
        Assert.Equal("hello", File.ReadAllText(path));
    }

    [Fact]
    public void CreateTruncatesALongerFileAndAppendKeepsItsBytes()
    {
        string path = PathIn("existing.txt");
        File.WriteAllText(path, "a much longer first content");

        Assert.True(PlainFile.TryWriteAllText(path, "short", FileMode.Create, out _));
        Assert.Equal("short", File.ReadAllText(path));
        Assert.True(PlainFile.TryWriteAllText(path, "+more", FileMode.Append, out _));
        Assert.Equal("short+more", File.ReadAllText(path));
    }

    [Fact]
    public void AnAppendToAMissingFileCreatesIt()
    {
        string path = PathIn("appended.txt");

        Assert.True(PlainFile.TryWriteAllText(path, "first", FileMode.Append, out _));

        Assert.Equal("first", File.ReadAllText(path));
    }

    [Fact]
    public void AnUnsupportedFileModeIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PlainFile.TryOpenWrite(PathIn("x"), FileMode.Open, out _));
    }

    [Fact]
    public void AFifoWithNoReaderIsRefusedPromptlyOnWrite()
    {
        string path = PathIn("pipe");
        PipeFiles.MakeFifo(path);

        (bool ok, string? refusal) = PipeFiles.Within(() => (PlainFile.TryWriteAllText(path, "x", FileMode.Create, out string? why), why));

        Assert.False(ok);
        Assert.Contains("not a regular file", refusal, StringComparison.Ordinal);
    }

    // With a reader attached the open succeeds, so only the type check on the handle stands between the text and the pipe.
    [Fact]
    public void AFifoWithAReaderAttachedIsStillRefusedOnWrite()
    {
        string path = PathIn("pipe");
        PipeFiles.MakeFifo(path);
        using FileStream reader = new(path, FileMode.Open, FileAccess.ReadWrite);

        (bool ok, string? refusal) = PipeFiles.Within(() => (PlainFile.TryWriteAllText(path, "must not arrive", FileMode.Append, out string? why), why));

        Assert.False(ok);
        Assert.Contains("not a regular file", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void ALinkToDevNullIsRefusedOnWrite()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("/dev/null is a Unix device.");

        string link = PathIn("null");
        File.CreateSymbolicLink(link, "/dev/null");

        (bool ok, string? refusal) = PipeFiles.Within(() => (PlainFile.TryWriteAllText(link, "x", FileMode.Create, out string? why), why));

        Assert.False(ok);
        Assert.Contains("not a regular file", refusal, StringComparison.Ordinal);
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void AnExistingFilesModeSurvivesAWrite()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("POSIX file modes do not exist on Windows.");

        string path = PathIn("private.txt");
        File.WriteAllText(path, "old");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        Assert.True(PlainFile.TryWriteAllText(path, "new", FileMode.Create, out _));

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.Equal("new", File.ReadAllText(path));
    }

    [Theory]
    [InlineData(FileMode.Create)]
    [InlineData(FileMode.Append)]
    public void ADirectoryIsRefusedAsNotARegularFileOnWrite(FileMode mode)
    {
        string directory = scratch.CreateDirectory();

        using FileStream? stream = PlainFile.TryOpenWrite(directory, mode, out string? refusal);
        bool ok = PlainFile.TryWriteAllText(directory, "x", mode, out string? writeRefusal);

        Assert.Null(stream);
        Assert.Contains("not a regular file", refusal, StringComparison.Ordinal);
        Assert.False(ok);
        Assert.Contains("not a regular file", writeRefusal, StringComparison.Ordinal);
    }
}
