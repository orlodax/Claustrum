using System.Text.Json;
using Claustrum.Cli;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// #130: `acp-opencode:<model>` is a backend:model spelling like any other, so `claustrum init --answers` accepts it for a model alias and writes it to the user config,
// and `acp-opencodex:` (which only looks like it) is refused as an unknown backend. Through the built binary, as InitEndToEndTests.
public sealed class AcpInitAliasEndToEndTests : IDisposable
{
    private readonly ClaustrumCli cli = new();

    public void Dispose() => cli.Dispose();

    private string Answers(string cheapCoding)
    {
        string path = Path.Combine(cli.Cwd, "answers.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["frontier-reasoning"] = "claude:opus",
            ["frontier-coding"] = "claude:opus",
            ["standard-coding"] = "claude:sonnet",
            ["cheap-coding"] = cheapCoding,
            ["fast"] = "claude:haiku",
        }));
        return path;
    }

    [Fact]
    public async Task AnAcpAliasTargetIsAcceptedAndWrittenToTheUserConfigAsync()
    {
        CliResult result = await cli.RunAsync(["init", "--answers", Answers("acp-opencode:openrouter/deepseek/deepseek-v4-flash#max")], "", null);

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        using JsonDocument config = JsonDocument.Parse(File.ReadAllText(cli.UserConfigPath));
        Assert.Equal("acp-opencode:openrouter/deepseek/deepseek-v4-flash#max", config.RootElement.GetProperty("models").GetProperty("cheap-coding").GetString());
    }

    [Fact]
    public async Task ANameThatOnlyLooksLikeTheAcpBackendIsRefusedAsUnknownAndNothingIsWrittenAsync()
    {
        CliResult result = await cli.RunAsync(["init", "--answers", Answers("acp-opencodex:model")], "", null);

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains("alias 'cheap-coding': unknown backend 'acp-opencodex' in 'acp-opencodex:model'", result.Stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(cli.UserConfigPath));
    }
}
