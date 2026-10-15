using Claustrum.Cli;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// #103 through the built binary: the cast verbs never read or write a cast through a link that leaves the cwd. `new`, `create`
// and `use` (and `show`) exit 2 with a message naming the file and its real path, the outside tree stays byte-identical and
// nothing is created there. The three shapes: `default.json` itself linked, `.claustrum` linked, `.claustrum/casts` linked.
// stdout/stderr have `\r\n` folded to `\n`; paths are matched case-insensitively on Windows.
public sealed class CastLinkEndToEndTests : IDisposable
{
    private const string Refusal = "resolves outside the working directory";
    private const string AllSupportedAnswers = "host\nclaude:opus\nnot needed\nnot needed\nclaude:sonnet\nclaude:sonnet\n2\n10\n";

    private readonly ClaustrumCli cli = ClaustrumCli.Initialized();
    private readonly CastLinkFixture links;
    private readonly string emptyPath;

    public CastLinkEndToEndTests()
    {
        links = new CastLinkFixture(cli.Cwd);
        emptyPath = Directory.CreateDirectory(Path.Combine(cli.Home, "empty-path")).FullName;
    }

    public void Dispose()
    {
        cli.Dispose();
        links.Dispose();
    }

    public static TheoryData<string, string> ShapesAndWritingVerbs()
    {
        TheoryData<string, string> data = [];
        foreach (string shape in CastLinkFixture.Shapes)
        {
            foreach (string verb in new[] { "new", "create", "use" })
                data.Add(shape, verb);
        }

        return data;
    }

    public static TheoryData<string> Shapes() => [.. CastLinkFixture.Shapes];

    private static string Lf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private Task<CliResult> RunAsync(string verb)
    {
        switch (verb)
        {
            case "new":
                return cli.RunAsync(["cast", "new"], AllSupportedAnswers, emptyPath);
            case "create":
                File.WriteAllText(Path.Combine(cli.Cwd, "answers.json"), /*lang=json,strict*/ """{"builder":"claude:opus"}""");
                return cli.RunAsync(["cast", "create", "--answers", "answers.json"], "", emptyPath);
            case "use":
                return cli.RunAsync(["cast", "use", "mine"], "", emptyPath);
            default:
                throw new ArgumentOutOfRangeException(nameof(verb), verb, "unknown verb");
        }
    }

    private static void AssertRefused(CliResult result, string realPath)
    {
        string stderr = Lf(result.Stderr);
        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains(Refusal, stderr, StringComparison.Ordinal);
        Assert.Contains(realPath, stderr, CastLinkFixture.PathComparison);
        Assert.DoesNotContain("Unhandled exception", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(CastLinkFixture.Secret.Trim(), Lf(result.Stdout) + stderr, StringComparison.Ordinal);
    }

    // `use` loads `mine` first: for the two directory links that read is what leaves the cwd, for the leaf link it is the write of `default`.
    // `new` lists the existing casts before it asks anything, so a linked directory is refused there, naming the directory.
    [Theory]
    [MemberData(nameof(ShapesAndWritingVerbs))]
    public async Task AWritingVerbRefusesALinkLeavingTheCwdAndLeavesTheOutsideByteIdenticalAsync(string shape, string verb)
    {
        links.Arrange(shape);
        string[] before = links.Snapshot();

        CliResult result = await RunAsync(verb);

        string refused = (verb, shape) switch
        {
            ("new", not "leaf") => Path.GetDirectoryName(links.RealPathFor("default")) ?? "",
            ("use", not "leaf") => links.RealPathFor("mine"),
            _ => links.RealPathFor("default"),
        };
        AssertRefused(result, refused);
        Assert.Equal(before, links.Snapshot());
        Assert.Equal(CastLinkFixture.Secret, File.ReadAllText(links.RealPathFor("default")));
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task CastShowRefusesALinkLeavingTheCwdAndPrintsNothingFromTheTargetAsync(string shape)
    {
        links.Arrange(shape);

        CliResult result = await cli.RunAsync(["cast", "show", "default"], "", emptyPath);

        AssertRefused(result, links.RealPathFor("default"));
        Assert.Equal("", result.Stdout.Trim());
    }

    // A `.claustrum` or `casts` link that nothing is written through yet still must not be created into: no cast, no outside directory.
    [Fact]
    public async Task CastCreateWithNoCastsDirectoryOutsideCreatesNothingThereAsync()
    {
        string target = Directory.CreateDirectory(Path.Combine(links.Outside, "bare")).FullName;
        TestLinks.CreateDirectoryLink(Path.Combine(cli.Cwd, ".claustrum"), target);

        CliResult result = await RunAsync("create");

        AssertRefused(result, Path.Combine(target, "casts", "default.json"));
        Assert.Empty(Directory.GetFileSystemEntries(target));
    }

    [Fact]
    public async Task ALeafLinkThatStaysInsideTheRepoIsWrittenThroughByCastUseAsync()
    {
        string real = Path.Combine(cli.Cwd, "kept.json");
        File.WriteAllText(real, CastLinkFixture.ValidCast);
        Directory.CreateDirectory(cli.CastsDirectory);
        File.WriteAllText(Path.Combine(cli.CastsDirectory, "mine.json"), CastLinkFixture.ValidCast.Replace("\"budget_usd\":null", "\"budget_usd\":3", StringComparison.Ordinal));
        TestLinks.Create(Path.Combine(cli.CastsDirectory, "default.json"), real);

        CliResult result = await RunAsync("use");

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.Contains("\"budget_usd\": 3", File.ReadAllText(real), StringComparison.Ordinal);
        Assert.Contains("\"name\": \"default\"", File.ReadAllText(real), StringComparison.Ordinal);
    }
}
