using System.Diagnostics;
using System.Runtime.InteropServices;
using Claustrum.Core.IO;
using Claustrum.Core.Tests.Testing;
// Claustrum.Core.Tests.Process is an enclosing-namespace member and wins over a using (see GitRepo): renamed.
using SystemProcess = System.Diagnostics.Process;

namespace Claustrum.Core.Tests.IO;

// #110 (2026-10-14): PlainFile.OpenNoFollowUnder reaches a file by an openat(O_NOFOLLOW) walk from a resolved root: a link on the
// way (leaf or intermediate) is a refusal, never a read, so the handle is the file a link-free path names however the names
// are swapped meanwhile. The handle must be a regular file with a single name. Unix only; the cases run on Linux and macOS in CI.
public sealed partial class PlainFileNoFollowTests : IDisposable
{
    private readonly ScratchRoot scratch = new("claustrum-nofollow-");

    public void Dispose() => scratch.Dispose();

    private static void RequireUnix()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("the no-follow walk is Unix only; Windows judges the opened handle's final path (ApiHouseRulesFenceTests).");
    }

    // The scratch root with every link in it resolved (macOS: /var/folders is /private/var/folders), as the product passes it.
    private string NewRoot() => RealDirectory(scratch.CreateDirectory());

    private static string RealDirectory(string directory)
    {
        ProcessStartInfo start = new("/bin/sh") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("cd \"$1\" && pwd -P");
        start.ArgumentList.Add("sh");
        start.ArgumentList.Add(directory);
        using SystemProcess process = SystemProcess.Start(start) ?? throw new InvalidOperationException("sh did not start");
        string output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return output;
    }

    private static string Write(string root, string content, params string[] relative)
    {
        string path = Path.Combine([root, .. relative]);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? root);
        File.WriteAllText(path, content);
        return path;
    }

    private static void HardLink(string existing, string link)
    {
        ProcessStartInfo start = new("ln") { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add(existing);
        start.ArgumentList.Add(link);
        using SystemProcess process = SystemProcess.Start(start) ?? throw new InvalidOperationException("ln did not start");
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            Assert.Skip($"a hard link cannot be made here: {error.Trim()}");
    }

    [Fact]
    public void APlainNestedFileOpensWithItsContent()
    {
        RequireUnix();
        string root = NewRoot();
        Write(root, "nested rules\n", "docs", "deep", "rules.md");

        using FileStream? stream = PlainFile.OpenNoFollowUnder(root, ["docs", "deep", "rules.md"], out string? refusal);

        Assert.NotNull(stream);
        Assert.Null(refusal);
        Assert.Equal("nested rules\n", new StreamReader(stream).ReadToEnd());
    }

    [Fact]
    public void AFileDirectlyUnderTheRootOpensAndAnEmptyFileIsEmpty()
    {
        RequireUnix();
        string root = NewRoot();
        Write(root, "", "AGENTS.md");

        using FileStream? stream = PlainFile.OpenNoFollowUnder(root, ["AGENTS.md"], out _);

        Assert.NotNull(stream);
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public void ALeafSymlinkIsRefusedEvenWhenItsTargetIsAnOrdinaryFileInside()
    {
        RequireUnix();
        string root = NewRoot();
        string target = Write(root, "x", "real.md");
        File.CreateSymbolicLink(Path.Combine(root, "link.md"), target);

        FileStream? stream = PlainFile.OpenNoFollowUnder(root, ["link.md"], out string? refusal);

        Assert.Null(stream);
        Assert.Contains("link", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void ADanglingLeafSymlinkIsRefusedNotMissing()
    {
        RequireUnix();
        string root = NewRoot();
        File.CreateSymbolicLink(Path.Combine(root, "link.md"), Path.Combine(root, "gone.md"));

        Assert.Null(PlainFile.OpenNoFollowUnder(root, ["link.md"], out string? refusal));
        Assert.NotNull(refusal);
    }

    [Fact]
    public void AnIntermediateDirectorySymlinkIsRefusedEvenWhenItStaysInside()
    {
        RequireUnix();
        string root = NewRoot();
        Write(root, "x", "real", "rules.md");
        Directory.CreateSymbolicLink(Path.Combine(root, "linked"), Path.Combine(root, "real"));

        FileStream? stream = PlainFile.OpenNoFollowUnder(root, ["linked", "rules.md"], out string? refusal);

        Assert.Null(stream);
        Assert.NotNull(refusal);
    }

    [Fact]
    public void AnIntermediateSymlinkToAnOutsideDirectoryIsRefused()
    {
        RequireUnix();
        string root = NewRoot();
        string outside = NewRoot();
        Write(outside, "secret", "rules.md");
        Directory.CreateSymbolicLink(Path.Combine(root, "linked"), outside);

        Assert.Null(PlainFile.OpenNoFollowUnder(root, ["linked", "rules.md"], out string? refusal));
        Assert.NotNull(refusal);
    }

    [Fact]
    public void AFileWithAnotherHardLinkIsRefusedAndASingleLinkFileIsAccepted()
    {
        RequireUnix();
        string root = NewRoot();
        string original = Write(root, "x", "rules.md");
        using (FileStream? single = PlainFile.OpenNoFollowUnder(root, ["rules.md"], out string? none))
        {
            Assert.NotNull(single);
            Assert.Null(none);
        }

        string other = Path.Combine(root, "other-name");
        HardLink(original, other);

        Assert.Null(PlainFile.OpenNoFollowUnder(root, ["rules.md"], out string? refusal));
        Assert.Equal(PlainFile.HardLinked, refusal);
        Assert.Null(PlainFile.OpenNoFollowUnder(root, ["other-name"], out string? sameFromTheOtherName));
        Assert.Equal(PlainFile.HardLinked, sameFromTheOtherName);

        File.Delete(other);
        using FileStream? again = PlainFile.OpenNoFollowUnder(root, ["rules.md"], out _);
        Assert.NotNull(again);
    }

    [Fact]
    public void AFifoLeafIsNotRegularAndNeverBlocks()
    {
        RequireUnix();
        string root = NewRoot();
        PipeFiles.MakeFifo(Path.Combine(root, "pipe.md"));

        (FileStream? stream, string? refusal) = PipeFiles.Within(() => (PlainFile.OpenNoFollowUnder(root, ["pipe.md"], out string? why), why));

        Assert.Null(stream);
        Assert.Equal(PlainFile.NotRegular, refusal);
    }

    [Fact]
    public void ABoundSocketLeafIsNotRegular()
    {
        RequireUnix();
        string root = NewRoot();
        using System.Net.Sockets.Socket socket = PipeFiles.BindSocket(Path.Combine(root, "sock.md"));

        (FileStream? stream, string? refusal) = PipeFiles.Within(() => (PlainFile.OpenNoFollowUnder(root, ["sock.md"], out string? why), why));

        Assert.Null(stream);
        Assert.Equal(PlainFile.NotRegular, refusal);
    }

    [Fact]
    public void ADirectoryLeafIsRefused()
    {
        RequireUnix();
        string root = NewRoot();
        Directory.CreateDirectory(Path.Combine(root, "AGENTS.md"));

        Assert.Null(PlainFile.OpenNoFollowUnder(root, ["AGENTS.md"], out string? refusal));
        Assert.Equal(PlainFile.NotRegular, refusal);
    }

    [Fact]
    public void ARegularFileWhereADirectoryIsExpectedIsRefused()
    {
        RequireUnix();
        string root = NewRoot();
        Write(root, "x", "plain");

        Assert.Null(PlainFile.OpenNoFollowUnder(root, ["plain", "rules.md"], out string? refusal));
        Assert.NotNull(refusal);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("/etc/passwd")]
    [InlineData("a\0b")]
    public void AComponentThatIsNotAPlainNameIsRefusedAnywhereInTheList(string component)
    {
        RequireUnix();
        string root = NewRoot();
        Write(root, "x", "a", "b");

        Assert.Null(PlainFile.OpenNoFollowUnder(root, [component], out string? alone));
        Assert.NotNull(alone);
        Assert.Null(PlainFile.OpenNoFollowUnder(root, ["a", component, "b"], out string? middle));
        Assert.NotNull(middle);
        Assert.Null(PlainFile.OpenNoFollowUnder(root, ["a", component], out string? last));
        Assert.NotNull(last);
    }

    // The walk starts at `/` and follows no link, the root's own ancestors included (review 2026-10-14): the root is a fully
    // resolved absolute path, and one that is not is refused, never opened.
    [Theory]
    [InlineData("relative/root")]
    [InlineData("")]
    [InlineData("./here")]
    public void ARootThatIsNotAbsoluteIsRefused(string root)
    {
        RequireUnix();

        Assert.Null(PlainFile.OpenNoFollowUnder(root, ["AGENTS.md"], out string? refusal));
        Assert.Contains("not a plain list of names", refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/tmp/../etc")]
    [InlineData("/tmp/./x")]
    public void ARootWithDotOrDotDotNamesIsRefusedBeforeAnythingIsOpened(string root)
    {
        RequireUnix();

        Assert.Null(PlainFile.OpenNoFollowUnder(root, ["passwd"], out string? refusal));
        Assert.Contains("not a plain list of names", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void ARootPassedThroughASymlinkedParentIsRefusedAndTheFileIsNotRead()
    {
        RequireUnix();
        string real = NewRoot();
        Write(real, "SECRET", "sub", "AGENTS.md");
        string holder = NewRoot();
        string linkedParent = Path.Combine(holder, "linked");
        Directory.CreateSymbolicLink(linkedParent, real);

        FileStream? stream = PlainFile.OpenNoFollowUnder(Path.Combine(linkedParent, "sub"), ["AGENTS.md"], out string? refusal);

        Assert.Null(stream);
        Assert.Contains("link", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void ARootThatIsItselfALinkIsRefused()
    {
        RequireUnix();
        string real = NewRoot();
        Write(real, "SECRET", "AGENTS.md");
        string link = Path.Combine(NewRoot(), "root-link");
        Directory.CreateSymbolicLink(link, real);

        Assert.Null(PlainFile.OpenNoFollowUnder(link, ["AGENTS.md"], out string? refusal));
        Assert.NotNull(refusal);
    }

    // A directory that can be searched but not read (mode 0311) is walked: the OS's path-only open does not need read permission.
    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void ASearchOnlyDirectoryOnTheWayIsWalkedOnLinux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Skip("the search-only open (O_PATH) is Linux's; macOS opens directories for reading and refuses by design.");

        string root = NewRoot();
        string file = Write(root, "x", "docs", "rules.md");
        string docs = Path.GetDirectoryName(file) ?? root;
        File.SetUnixFileMode(docs, UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        try
        {
            try
            {
                _ = Directory.GetFileSystemEntries(docs);
                Assert.Skip("this account lists a mode-0311 directory (root, or a capability that bypasses modes).");
            }
            catch (UnauthorizedAccessException)
            {
                // The point of the case.
            }

            using FileStream? stream = PlainFile.OpenNoFollowUnder(root, ["docs", "rules.md"], out string? refusal);

            Assert.NotNull(stream);
            Assert.Null(refusal);
        }
        finally
        {
            File.SetUnixFileMode(docs, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void AnEmptyComponentListIsRefused()
    {
        RequireUnix();

        Assert.Null(PlainFile.OpenNoFollowUnder(NewRoot(), [], out string? refusal));
        Assert.NotNull(refusal);
    }

    [Fact]
    public void AMissingFileOrDirectoryThrowsFileNotFound()
    {
        RequireUnix();
        string root = NewRoot();
        Write(root, "x", "docs", "there.md");

        Assert.Throws<FileNotFoundException>(() => PlainFile.OpenNoFollowUnder(root, ["docs", "missing.md"], out _));
        Assert.Throws<FileNotFoundException>(() => PlainFile.OpenNoFollowUnder(root, ["nodocs", "there.md"], out _));
        Assert.Throws<FileNotFoundException>(() => PlainFile.OpenNoFollowUnder(Path.Combine(root, "no-such-root"), ["x"], out _));
    }

    // The attack the walk exists for: the leaf name flips between the file and a link to an outside secret as fast as a writer can
    // exchange them. Whatever the reader gets is the file or a refusal, never the secret. renameat2(RENAME_EXCHANGE) swaps
    // the two names atomically, so the name is never absent and every open lands on one or the other.
    [Fact]
    public void ALeafExchangedAtomicallyWithALinkToASecretNeverYieldsTheSecret()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Skip("renameat2(RENAME_EXCHANGE) is Linux only.");

        string root = NewRoot();
        string secret = Write(NewRoot(), "TOP-SECRET", "secret.md");
        string rules = Write(root, "REGULAR", "AGENTS.md");
        string staging = Path.Combine(root, "staging.md");
        File.CreateSymbolicLink(staging, secret);

        bool stop = false;
        int exchanges = 0;
        Thread flipper = new(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                if (RenameAt2(AtFdCwd, rules, AtFdCwd, staging, RenameExchange) == 0)
                    Interlocked.Increment(ref exchanges);
            }
        })
        {
            IsBackground = true,
        };
        flipper.Start();

        int opened = 0;
        int refused = 0;
        Stopwatch clock = Stopwatch.StartNew();
        try
        {
            while (clock.Elapsed < TimeSpan.FromSeconds(3))
            {
                using FileStream? stream = PlainFile.OpenNoFollowUnder(root, ["AGENTS.md"], out _);
                if (stream is null)
                {
                    refused++;
                    continue;
                }

                Assert.Equal("REGULAR", new StreamReader(stream).ReadToEnd());
                opened++;
            }
        }
        finally
        {
            Volatile.Write(ref stop, true);
            flipper.Join(TimeSpan.FromSeconds(10));
        }

        Assert.True(exchanges > 100, $"only {exchanges} exchanges happened, so the loop proved nothing");
        Assert.True(opened > 0, "the regular file was never opened");
        Assert.True(refused > 0, "the link was never met, so the loop proved nothing");
    }

    private const int AtFdCwd = -100;
    private const uint RenameExchange = 2;

    [LibraryImport("libc", EntryPoint = "renameat2", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int RenameAt2(int oldDirectory, string oldPath, int newDirectory, string newPath, uint flags);
}
