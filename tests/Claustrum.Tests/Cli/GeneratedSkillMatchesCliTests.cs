using System.CommandLine;
using System.Reflection;
using System.Text.RegularExpressions;
using Claustrum.Cli;
using Claustrum.Mcp;
using Claustrum.Roles;
using Claustrum.Roles.Sync;
using ModelContextProtocol.Server;

namespace Claustrum.Tests.Cli;

// Review finding #2: the generated `.claude/skills/claustrum/SKILL.md` told agents to run
// `claustrum cast create --answers <file>`, which the CLI rejected — the skill text and the CLI
// grammar drifted apart with nothing checking they agreed. These tests close that class: every
// command the generated skill tells an agent to run is parsed against the real root command, and
// every MCP tool it names must exist on ClaustrumTools.
public sealed partial class GeneratedSkillMatchesCliTests : IDisposable
{
    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-skill-").FullName;
    private readonly string fakeHome = Directory.CreateTempSubdirectory("claustrum-skill-home-").FullName;

    public void Dispose()
    {
        Directory.Delete(cwd, recursive: true);
        Directory.Delete(fakeHome, recursive: true);
    }

    [Fact]
    public void EveryClaustrumCommandInTheGeneratedSkillParsesAgainstTheRealCliGrammar()
    {
        string[] commands = SkillCommands();

        // The extractor is the weak link: an over-eager regex change that stops matching would make
        // the assertions below vacuous, so pin the count the skill actually documents today.
        Assert.Equal(3, commands.Length);

        RootCommand root = CliRoot.Build();
        foreach (string command in commands)
        {
            ParseResult parsed = root.Parse(Tokenize(command));
            Assert.True(
                parsed.Errors.Count == 0,
                $"SKILL.md documents `claustrum {command}`, which the CLI rejects: {string.Join("; ", parsed.Errors.Select(e => e.Message))}");
        }
    }

    [Fact]
    public void TheSkillDocumentsCastCreateWithAnAnswersOption()
    {
        Assert.Contains(SkillCommands(), command => command.StartsWith("cast create --answers ", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryMcpToolTheSkillNamesExistsOnClaustrumTools()
    {
        string[] named = [.. NamedToolPattern().Matches(GeneratedSkill()).Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal)];
        Assert.Equal(3, named.Length);

        foreach (string tool in named)
            Assert.Contains(tool, McpToolNames());
    }

    private string GeneratedSkill()
    {
        RoleLibrary library = new();
        new ClaudeSync(library, new RoleRenderer(library), fakeHome).Sync(cwd, roles: ["builder"]);

        return File.ReadAllText(Path.Combine(cwd, ".claude", "skills", "claustrum", "SKILL.md"));
    }

    // Inline `claustrum ...` spans plus fenced-block lines starting with it. A leading backtick (or
    // line start) is what keeps `.claustrum/casts/default.json` from matching.
    private string[] SkillCommands() =>
        [.. CommandPattern().Matches(GeneratedSkill())
            .Select(match => Collapse(match.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)];

    // Placeholders become a literal, optional `[...]` groups lose their brackets: both are prose
    // conventions of the skill text, not part of what the parser has to accept.
    private static string[] Tokenize(string command) =>
        [.. command.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.Trim('[', ']'))
            .Select(token => token.StartsWith('<') ? "placeholder" : token)];

    private static string Collapse(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string[] McpToolNames() =>
        [.. typeof(ClaustrumTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .OfType<string>()];

    [GeneratedRegex(@"(?:`|^)claustrum ([^`\r\n]+)`?", RegexOptions.Multiline)]
    private static partial Regex CommandPattern();

    [GeneratedRegex(@"`([a-z][a-z0-9_]*)`\s+tool")]
    private static partial Regex NamedToolPattern();
}
