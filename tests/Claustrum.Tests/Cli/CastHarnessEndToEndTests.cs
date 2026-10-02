using System.Text.Json;
using Claustrum.Cli;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// The cast verbs' half of #43, through the real built binary: `cast create` and `cast new` refuse an
// answer that puts a role on a harness its role.json does not list (exit 2, nothing saved), `cast new`
// never re-asks a piped line, and `cast questions` explains an empty option list instead of printing
// nothing. PATH is emptied for the child, so no backend on the machine is "installed" unless a test hands
// it a fake `claude` script through claustrum.json — the same override CoordinateEndToEndTests uses.
public sealed class CastHarnessEndToEndTests : IDisposable
{
    // Question order is the library's (alphabetical roles) with the fixed ones around it: architect,
    // builder, code-reviewer, demo-author, tester, ui-reviewer, builder_max_parallel, budget.
    private const string AllSupportedAnswers = "host\nclaude:opus\nnot needed\nnot needed\nclaude:sonnet\nclaude:sonnet\n2\n10\n";

    private readonly ClaustrumCli cli = new();
    private readonly string emptyPath;

    public CastHarnessEndToEndTests() => emptyPath = Directory.CreateDirectory(Path.Combine(cli.Home, "empty-path")).FullName;

    public void Dispose() => cli.Dispose();

    private Task<CliResult> RunAsync(string[] args, string stdin = "") => cli.RunAsync(args, stdin, emptyPath);

    private string DefaultCastPath => Path.Combine(cli.CastsDirectory, "default.json");

    private async Task<CliResult> CastCreateAsync(string answersJson)
    {
        File.WriteAllText(Path.Combine(cli.Cwd, "answers.json"), answersJson);
        return await RunAsync(["cast", "create", "--answers", "answers.json"]);
    }

    [Fact]
    public async Task CastCreateRefusesADemoAuthorOnAnotherHarnessExitingTwoAndSavingNothingAsync()
    {
        CliResult result = await CastCreateAsync(/*lang=json,strict*/ """{"builder":"claude:opus","demo-author":"opencode:some-model"}""");

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains("cast 'default' not saved:", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("'opencode:some-model' puts demo-author on 'opencode', but demo-author runs on claude only", result.Stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(DefaultCastPath));
        Assert.False(Directory.Exists(cli.CastsDirectory));
    }

    [Fact]
    public async Task CastCreateRefusesAnArchitectSpawnedOnApiAsync()
    {
        CliResult result = await CastCreateAsync(/*lang=json,strict*/ """{"architect":"spawned on api:some-model","builder":"claude:opus"}""");

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains("puts architect on 'api'", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("or 'host'", result.Stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(DefaultCastPath));
    }

    [Fact]
    public async Task CastCreateNamesEveryOffendingRoleInOneRunAsync()
    {
        CliResult result = await CastCreateAsync(/*lang=json,strict*/ """{"builder":"api:b","demo-author":"cursor:c","ui-reviewer":"copilot:d"}""");

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains("puts builder on 'api'", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("puts demo-author on 'cursor'", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("puts ui-reviewer on 'copilot'", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CastCreateSavesADemoAuthorThatIsNotNeededAsync()
    {
        CliResult result = await CastCreateAsync(/*lang=json,strict*/ """{"builder":"claude:opus","demo-author":"not needed","ui-reviewer":"claude:sonnet"}""");

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        using JsonDocument cast = JsonDocument.Parse(File.ReadAllText(DefaultCastPath));
        JsonElement roles = cast.RootElement.GetProperty("roles");
        Assert.Equal(JsonValueKind.Null, roles.GetProperty("demo-author").ValueKind);
        Assert.Equal("claude:sonnet", roles.GetProperty("ui-reviewer").GetProperty("model").GetString());
    }

    // A backend name Claustrum does not register is left to the run, so the cast may hold it.
    [Fact]
    public async Task CastCreateSavesAnEntryOnAnUnregisteredBackendAsync()
    {
        CliResult result = await CastCreateAsync(/*lang=json,strict*/ """{"builder":"nonexistent:some-model","demo-author":"nonexistent:other-model"}""");

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.True(File.Exists(DefaultCastPath), result.Stderr);
    }

    // Config.Load runs in `cast create` now, so a claustrum.json that cannot be read stops it, naming the
    // file — even for an answers file that would have been fine.
    [Fact]
    public async Task CastCreateOnAMalformedClaustrumJsonExitsTwoNamingTheFileAndSavesNothingAsync()
    {
        cli.MarkAsGitRoot();
        File.WriteAllText(Path.Combine(cli.Cwd, "claustrum.json"), "{ not json");

        CliResult result = await CastCreateAsync(/*lang=json,strict*/ """{"builder":"claude:opus"}""");

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains("claustrum.json", result.Stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(DefaultCastPath));
    }

    // The 2026-10-02 review's exact input: the fourth line names the demo-author on opencode. On a pipe
    // there is no one to ask again, so it ends the run — before, it re-asked and every later answer slid
    // one question up (ui-reviewer was saved as "2").
    [Fact]
    public async Task CastNewOnPipedInputRefusesTheDemoAuthorOnOpencodeExitingTwoAndSavingNothingAsync()
    {
        CliResult result = await RunAsync(["cast", "new"], "host\nclaude:opus\nnot needed\nopencode:x\nclaude:sonnet\nclaude:sonnet\n2\n10\n");

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains("cast not saved:", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("'opencode:x' puts demo-author on 'opencode'", result.Stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(DefaultCastPath));
        Assert.False(Directory.Exists(cli.CastsDirectory));

        // The run stopped at that question: nothing after the demo-author's was asked.
        Assert.Contains("demo-author>", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("tester>", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CastNewOnPipedInputWithOnlySupportedAnswersSavesEachAnswerToItsOwnQuestionAsync()
    {
        CliResult result = await RunAsync(["cast", "new"], AllSupportedAnswers);

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        using JsonDocument cast = JsonDocument.Parse(File.ReadAllText(DefaultCastPath));
        JsonElement roles = cast.RootElement.GetProperty("roles");
        Assert.Equal("host", cast.RootElement.GetProperty("architect").GetProperty("mode").GetString());
        Assert.Equal("claude:opus", roles.GetProperty("builder").GetProperty("model").GetString());
        Assert.Equal(2, roles.GetProperty("builder").GetProperty("max_parallel").GetInt32());
        Assert.Equal(JsonValueKind.Null, roles.GetProperty("code-reviewer").ValueKind);
        Assert.Equal(JsonValueKind.Null, roles.GetProperty("demo-author").ValueKind);
        Assert.Equal("claude:sonnet", roles.GetProperty("tester").GetProperty("model").GetString());
        Assert.Equal("claude:sonnet", roles.GetProperty("ui-reviewer").GetProperty("model").GetString());
        Assert.Equal(10m, cast.RootElement.GetProperty("budget_usd").GetDecimal());
    }

    // The architect's own answer is judged once the whole cast is built (it is not a plain role key), so
    // the questions all run first and the refusal comes at the end — still exit 2, still nothing saved.
    [Fact]
    public async Task CastNewOnPipedInputRefusesAnArchitectSpawnedOnApiAtTheEndAsync()
    {
        CliResult result = await RunAsync(["cast", "new"], "spawned on api:some-model\nclaude:opus\nnot needed\nnot needed\nclaude:sonnet\nclaude:sonnet\n1\nno cap\n");

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains("puts architect on 'api'", result.Stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(DefaultCastPath));
    }

    // End of input must end the questionnaire: a blank answer is "no answer" (CastHarnessCheck.Problem is
    // null for it), not a reason to ask again. The helper kills a run that outlives its timeout, so a
    // loop shows up as the timeout failure rather than a stuck suite.
    [Fact]
    public async Task CastNewAtEndOfInputTerminatesRatherThanAskingAgainAsync()
    {
        CliResult result = await RunAsync(["cast", "new"]);

        Assert.True(result.ExitCode is ExitCodes.Ok or ExitCodes.Usage, $"exit {result.ExitCode}: {result.Stderr}");
    }

    [Fact]
    public async Task CastQuestionsExplainsAnEmptyOptionListAndKeepsNotNeededForTheBrowserRolesAsync()
    {
        CliResult result = await RunAsync(["cast", "questions"]);

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Dictionary<string, string> options = OptionsLines(result.Stdout);

        foreach (string role in new[] { "demo-author", "ui-reviewer", "tester", "code-reviewer" })
        {
            Assert.Contains("none here", options[role], StringComparison.Ordinal);
            Assert.Contains("free-form", options[role], StringComparison.Ordinal);
            Assert.EndsWith(", or 'not needed'", options[role], StringComparison.Ordinal);
        }

        // The builder is the one role that may not be skipped: the explanation, and no way out.
        Assert.Contains("none here", options["builder"], StringComparison.Ordinal);
        Assert.Contains("free-form", options["builder"], StringComparison.Ordinal);
        Assert.DoesNotContain("not needed", options["builder"], StringComparison.Ordinal);
    }

    // With claude "installed" (a script that answers --version), the aliases it serves are listed, and
    // 'not needed' now rides on the same line instead of on a line of its own.
    [Fact]
    public async Task CastQuestionsPrintsTheAliasesAndNotNeededOnOneLineWhenClaudeIsInstalledAsync()
    {
        InstallFakeClaude();

        CliResult result = await RunAsync(["cast", "questions"]);

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Dictionary<string, string> options = OptionsLines(result.Stdout);
        const string aliases = "cheap-coding, fast, frontier-coding, frontier-reasoning, standard-coding";
        Assert.Equal($"  options: {aliases}", options["builder"]);
        Assert.Equal($"  options: {aliases}, or 'not needed'", options["demo-author"]);
        Assert.Equal($"  options: {aliases}, or 'not needed'", options["ui-reviewer"]);
        Assert.Equal(
            "  options: host, spawned on cheap-coding, spawned on fast, spawned on frontier-coding, spawned on frontier-reasoning, spawned on standard-coding",
            options["architect"]);
        Assert.DoesNotContain("(or 'not needed')", result.Stdout, StringComparison.Ordinal);
    }

    // `cast questions --json` carries the same filtered options for a tool to read.
    [Fact]
    public async Task CastQuestionsJsonCarriesTheSameOptionsAndTheWhereItRunsPromptAsync()
    {
        InstallFakeClaude();

        CliResult result = await RunAsync(["cast", "questions", "--json"]);

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement[] questions = [.. document.RootElement.GetProperty("questions").EnumerateArray()];
        JsonElement demoAuthor = questions.Single(question => question.GetProperty("key").GetString() == "demo-author");
        Assert.Contains("It runs on claude only.", demoAuthor.GetProperty("prompt").GetString(), StringComparison.Ordinal);
        Assert.Equal(5, demoAuthor.GetProperty("options").GetArrayLength());
    }

    // The option line follows its question line; the key is the text before the first colon.
    private static Dictionary<string, string> OptionsLines(string stdout)
    {
        string[] lines = [.. stdout.Split('\n').Select(line => line.TrimEnd('\r'))];
        Dictionary<string, string> options = [];
        for (int i = 0; i < lines.Length - 1; i++)
        {
            if (lines[i + 1].StartsWith("  options:", StringComparison.Ordinal))
                options[lines[i][..lines[i].IndexOf(':', StringComparison.Ordinal)]] = lines[i + 1];
        }

        return options;
    }

    // `backends.claude.path` pointed at a script that answers `--version`: BinaryLocator honours the
    // override ahead of PATH, so claude is "found" with PATH emptied and nothing real is started.
    private void InstallFakeClaude()
    {
        string script = Path.Combine(cli.Cwd, OperatingSystem.IsWindows() ? "fake-claude.cmd" : "fake-claude.sh");
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(script, "@echo off\r\necho 1.0.0\r\n");
        }
        else
        {
            File.WriteAllText(script, "#!/bin/sh\necho 1.0.0\n");
            File.SetUnixFileMode(script,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        cli.MarkAsGitRoot();

        // A placeholder + Replace, as CoordinateEndToEndTests does: a raw-string hole next to literal braces is CS9007.
        const string template = /*lang=json,strict*/ """{"backends":{"claude":{"path":"SCRIPT_PATH"}}}""";
        File.WriteAllText(Path.Combine(cli.Cwd, "claustrum.json"), template.Replace("SCRIPT_PATH", script.Replace("\\", "\\\\", StringComparison.Ordinal), StringComparison.Ordinal));
    }
}
