using System.Text;
using Claustrum.Roles.Sync;

namespace Claustrum.Roles.Tests.Sync;

// Issue #33: ClaudeSync, OpencodeSync and CursorSync emitted `description: <text>` unquoted, and a
// description containing ": " is not a valid plain YAML scalar (copilot 1.0.87 refused it, measured
// 2026-09-22). Every sync now goes through SyncWriter.YamlQuoted. There is no YAML library in the
// test project, so UnquoteDoubleQuoted below is a deliberately strict reader of exactly the subset
// YamlQuoted may emit: any raw `"` before the end, a lone `\`, or an escape other than `\\`/`\"`
// is a parse error — which is what a strict parser would also say.
public sealed class DescriptionQuotingTests : IDisposable
{
    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-quoting-").FullName;
    private readonly string fakeHome = Directory.CreateTempSubdirectory("claustrum-quoting-home-").FullName;
    private readonly RoleLibrary library = new();

    public void Dispose()
    {
        Directory.Delete(cwd, recursive: true);
        Directory.Delete(fakeHome, recursive: true);
    }

    public static TheoryData<string> Harnesses => ["claude", "opencode", "cursor", "copilot"];

    // The fully rendered directory holds only agents (skills/commands live in sibling directories).
    private static string AgentDirectory(string harness) => harness switch
    {
        "claude" => Path.Combine(".claude", "agents"),
        "opencode" => Path.Combine(".opencode", "agent"),
        "cursor" => Path.Combine(".cursor", "agents"),
        "copilot" => Path.Combine(".github", "agents"),
        _ => throw new ArgumentOutOfRangeException(nameof(harness), harness, null),
    };

    private SyncResult Sync(string harness, SyncMode mode = SyncMode.Write)
    {
        RoleRenderer renderer = new(library);
        return harness switch
        {
            "claude" => new ClaudeSync(library, renderer, fakeHome).Sync(cwd, mode: mode),
            "opencode" => new OpencodeSync(library, renderer, fakeHome).Sync(cwd, mode: mode),
            "cursor" => new CursorSync(library, renderer, fakeHome).Sync(cwd, mode: mode),
            "copilot" => new CopilotSync(library, renderer, fakeHome).Sync(cwd, mode: mode),
            _ => throw new ArgumentOutOfRangeException(nameof(harness), harness, null),
        };
    }

    private static string DescriptionLine(string path)
    {
        List<string> lines = [.. File.ReadAllText(path).Split('\n').TakeWhile((line, i) => i == 0 || line != "---")];
        Assert.Equal("---", lines[0]);
        return lines.Single(line => line.StartsWith("description: ", StringComparison.Ordinal));
    }

    private static string UnquoteDoubleQuoted(string scalar)
    {
        Assert.True(scalar.Length >= 2 && scalar[0] == '"' && scalar[^1] == '"', $"not a double-quoted scalar: {scalar}");
        StringBuilder value = new();
        for (int i = 1; i < scalar.Length - 1; i++)
        {
            char c = scalar[i];
            Assert.NotEqual('"', c);
            if (c != '\\')
            {
                value.Append(c);
                continue;
            }

            i++;
            Assert.True(i < scalar.Length - 1, $"dangling backslash: {scalar}");
            Assert.True(scalar[i] is '\\' or '"', $"unexpected escape \\{scalar[i]}: {scalar}");
            value.Append(scalar[i]);
        }

        return value.ToString();
    }

    [Theory]
    [InlineData("plain description")]
    [InlineData("Use for X: it does Y")]
    [InlineData("Invoke explicitly as \"architect\" when you want it")]
    [InlineData("a back\\slash and a trailing one\\")]
    [InlineData("both \"quote\" and \\ and: colon")]
    [InlineData("\\\"")]
    [InlineData("ends with a quote\"")]
    [InlineData("- starts like a list: yes # and a comment? {braces} [brackets] & *anchor !tag @ `tick`")]
    public void YamlQuotedRoundTripsAndStaysOnOneLine(string source)
    {
        string quoted = SyncWriter.YamlQuoted(source);

        Assert.DoesNotContain('\n', quoted);
        Assert.Equal(source, UnquoteDoubleQuoted(quoted));
    }

    [Fact]
    public void YamlQuotedLeavesADescriptionWithNeitherSpecialCharacterVerbatimInsideTheQuotes()
    {
        Assert.Equal("\"plain description, with commas.\"", SyncWriter.YamlQuoted("plain description, with commas."));
    }

    [Theory]
    [MemberData(nameof(Harnesses))]
    public void EveryGeneratedAgentFileCarriesAParseableQuotedDescriptionOnOneLine(string harness)
    {
        Sync(harness);

        string[] files = Directory.GetFiles(Path.Combine(cwd, AgentDirectory(harness)));
        Assert.NotEmpty(files);
        foreach (string file in files)
        {
            string line = DescriptionLine(file);
            string value = UnquoteDoubleQuoted(line["description: ".Length..]);
            Assert.False(string.IsNullOrWhiteSpace(value), file);
        }
    }

    // architect is the anchor: its description carries both a ": " and an inner `"`, and it is a
    // role every one of the four harnesses renders.
    [Theory]
    [MemberData(nameof(Harnesses))]
    public void ArchitectDescriptionWithColonSpaceAndInnerQuotesParsesBackToItsSource(string harness)
    {
        Sync(harness);

        string suffix = harness == "copilot" ? ".agent.md" : ".md";
        string line = DescriptionLine(Path.Combine(cwd, AgentDirectory(harness), $"architect{suffix}"));
        string value = UnquoteDoubleQuoted(line["description: ".Length..]);

        Assert.Contains("It does not ship production code itself: it hands implementation", value, StringComparison.Ordinal);
        Assert.Contains("Invoke explicitly as \"architect\" when", value, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Harnesses))]
    public void TierStubDescriptionIsQuotedAndRoundTripsToTheSharedText(string harness)
    {
        Sync(harness);

        string suffix = harness == "copilot" ? ".agent.md" : ".md";
        foreach (string tier in new[] { "xhigh", "max" })
        {
            string line = DescriptionLine(Path.Combine(cwd, AgentDirectory(harness), $"builder-{tier}{suffix}"));
            Assert.Equal(SyncWriter.TierDescription("builder", tier, true), UnquoteDoubleQuoted(line["description: ".Length..]));
        }
    }

    // The whole role set, not just builder: the roles with a ": " in the description are the ones
    // whose bytes changed, and `sync --check` must still agree with `sync` about them.
    [Theory]
    [MemberData(nameof(Harnesses))]
    public void SyncedTwiceAndThenCheckedReportsNothingOutstanding(string harness)
    {
        Sync(harness);
        SyncResult second = Sync(harness);
        SyncResult check = Sync(harness, SyncMode.Check);

        Assert.Empty(second.Written);
        Assert.Empty(second.Foreign);
        Assert.NotEmpty(second.Skipped);
        Assert.Empty(check.Written);
        Assert.Empty(check.Foreign);
        Assert.NotEmpty(check.Skipped);
    }
}
