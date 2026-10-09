using Claustrum.Roles.Sync;

namespace Claustrum.Roles.Tests;

// #90: `claustrum run` blocks until its role is done, so a spawned architect that called it twice in a row ran its
// two `max_parallel: 2` builders one after the other (drive 2, 2026-10-09). The architect's Delegation contract now
// carries the recipe — background each run, redirect its receipt, `wait` — in both parts (delegation.claude.md for
// claude, delegation.default.md for every other harness), and ROLE.md's "Fan builders out" bullet points at it.
// Claustrum.Tests' CoordinationTextAgreementTests keeps ROLE.md and the spawned appendix saying the same sentence.
public sealed class ConcurrentBuildersRecipeTests : IDisposable
{
    private const string Pointer = "to actually run builders at once, start the runs in the background and `wait` (the recipe is in your Delegation contract)";

    private const string WaitBlock = """
        claustrum run builder --cast "<cast>" --brief-file .claustrum/briefs/1-builder.md --json \
          --cwd "<dir>" > .claustrum/briefs/1-builder.result.json &
        claustrum run builder --cast "<cast>" --brief-file .claustrum/briefs/2-builder.md --json \
          --cwd "<dir>" > .claustrum/briefs/2-builder.result.json &
        wait
        """;

    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-concurrent-recipe-").FullName;
    private readonly string fakeHome = Directory.CreateTempSubdirectory("claustrum-concurrent-recipe-home-").FullName;
    private readonly RoleLibrary library = new();

    public void Dispose()
    {
        Directory.Delete(cwd, recursive: true);
        Directory.Delete(fakeHome, recursive: true);
    }

    private string RawBody(string tier, string harness) => new RoleRenderer(library).Render("architect", tier, harness, cwd).SystemBody;

    private string Body(string tier, string harness) => Prose.Flatten(RawBody(tier, harness));

    public static TheoryData<string> Harnesses => ["claude", "opencode", "cursor", "copilot"];

    [Theory]
    [MemberData(nameof(Harnesses))]
    public void TheRecipeBacksEachRunWithAmpersandRedirectsItsReceiptAndWaitsOnEveryHarness(string harness)
    {
        string raw = RawBody("high", harness);

        // Indentation of the part's list-item continuation lines is not part of the recipe.
        string unindented = Unindent(raw);
        Assert.Contains(Unindent(WaitBlock), unindented, StringComparison.Ordinal);
        Assert.Equal(1, Count(unindented, "\nwait\n"));
        Assert.Contains("Never start more at once than the cast's `max_parallel`.", Prose.Flatten(raw), StringComparison.Ordinal);
        Assert.Contains("64 ms apart", Prose.Flatten(raw), StringComparison.Ordinal);
    }

    [Fact]
    public void TheClaudePartNamesTheBashToolBackgroundModeAndTheDefaultPartTheHarnessOwn()
    {
        string claude = Body("high", "claude");
        string other = Body("high", "opencode");

        Assert.Contains("**Running builders concurrently through Claustrum.**", claude, StringComparison.Ordinal);
        Assert.Contains("The Bash tool's `run_in_background: true` is the other way to start them — but read every receipt before your turn ends.", claude, StringComparison.Ordinal);
        Assert.DoesNotContain("**Running builders concurrently from a shell.**", claude, StringComparison.Ordinal);

        Assert.Contains("**Running builders concurrently from a shell.**", other, StringComparison.Ordinal);
        Assert.Contains("use your harness's own background-command mode when it has one, reading every receipt before you finish", other, StringComparison.Ordinal);
        Assert.DoesNotContain("**Running builders concurrently through Claustrum.**", other, StringComparison.Ordinal);
        Assert.DoesNotContain("run_in_background", other, StringComparison.Ordinal);
    }

    // The claude part used to end the cast bullet with "The rest of this section applies only when no cast is in
    // play" — which now has to follow the recipe, or the recipe reads as native-subagent-only advice.
    [Fact]
    public void TheClaudeRecipeSitsInsideTheCastBulletBeforeTheNoCastBranchBegins()
    {
        string body = Body("high", "claude");
        int recipe = body.IndexOf("**Running builders concurrently through Claustrum.**", StringComparison.Ordinal);
        int noCast = body.IndexOf("The rest of this section applies only when **no** cast is in play.", StringComparison.Ordinal);
        int nativeSpawns = body.IndexOf("**Each delegate is a native subagent spawn:**", StringComparison.Ordinal);

        Assert.True(recipe >= 0 && recipe < noCast && noCast < nativeSpawns, $"recipe {recipe}, no-cast sentence {noCast}, native spawns {nativeSpawns}");
        Assert.Equal(1, Count(body, "The rest of this section applies only when **no** cast is in play."));
    }

    [Theory]
    [MemberData(nameof(Harnesses))]
    public void TheFanBuildersOutBulletPointsAtTheRecipeInTheDelegationContract(string harness)
    {
        string body = Body("high", harness);

        Assert.Equal(1, Count(body, Pointer));
        int bullet = body.IndexOf("Fan builders out", StringComparison.Ordinal);
        int pointer = body.IndexOf(Pointer, StringComparison.Ordinal);
        int recipe = body.IndexOf("Running builders concurrently", StringComparison.Ordinal);
        Assert.True(bullet >= 0 && bullet < pointer, "the pointer belongs to the max_parallel bullet");
        Assert.True(pointer < recipe, "the pointer comes before the Delegation contract it names");
    }

    // Tier stubs are a frontmatter and a pointer to the base agent: none of the role's text, so none of the recipe.
    [Theory]
    [InlineData("architect-xhigh.md")]
    [InlineData("architect-max.md")]
    public void TheTierStubsCarryNeitherTheRecipeNorThePointer(string stub)
    {
        ClaudeSync sync = new(library, new RoleRenderer(library), fakeHome);
        sync.Sync(cwd, roles: ["architect"]);

        string content = Prose.Flatten(File.ReadAllText(Path.Combine(cwd, ".claude", "agents", stub)));

        Assert.Contains("claustrum:generated", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Running builders concurrently", content, StringComparison.Ordinal);
        Assert.DoesNotContain(Pointer, content, StringComparison.Ordinal);
    }

    // The base agent file is the one that carries it: the golden is byte-compared elsewhere, this names why it moved.
    [Fact]
    public void TheBaseClaudeAgentFileCarriesTheRecipeAndThePointer()
    {
        ClaudeSync sync = new(library, new RoleRenderer(library), fakeHome);
        sync.Sync(cwd, roles: ["architect"]);

        string content = Prose.Flatten(File.ReadAllText(Path.Combine(cwd, ".claude", "agents", "architect.md")));

        Assert.Contains("**Running builders concurrently through Claustrum.**", content, StringComparison.Ordinal);
        Assert.Contains(Pointer, content, StringComparison.Ordinal);
    }

    private static string Unindent(string text) => string.Join('\n', text.Split('\n').Select(line => line.TrimStart()));

    private static int Count(string text, string needle) => text.Split(needle).Length - 1;
}
