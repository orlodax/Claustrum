namespace Claustrum.Tests.Testing;

// The wrapper forwards to the real platform for everything but the home directory, so a FIFO among the files it reads
// is refused through it as well, not read by a plain ReadAllText that would block (2026-10-14).
public sealed class HomeRedirectPlatformTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("claustrum-home-redirect-").FullName;

    public void Dispose() => TempTree.Delete(directory);

    [Fact]
    public void ReadRegularFileTextOnAFifoIsNullWithARefusal()
    {
        string path = Path.Combine(directory, "pipe");
        PipeFiles.MakeFifo(path);
        HomeRedirectPlatform platform = new(directory);

        (string? text, string? refusal) = PipeFiles.Within(() => (platform.ReadRegularFileText(path, out string? why), why));

        Assert.Null(text);
        Assert.Contains("not a regular file", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadRegularFileTextOnARegularFileReadsIt()
    {
        string path = Path.Combine(directory, "plain.txt");
        File.WriteAllText(path, "hello");

        Assert.Equal("hello", new HomeRedirectPlatform(directory).ReadRegularFileText(path, out _));
    }
}
