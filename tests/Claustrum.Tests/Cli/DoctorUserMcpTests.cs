using Claustrum.Cli;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// #128: `backends doctor` prints the merged config with the layer each key came from, and roles.<role>.user_mcp is one of
// the keys. PATH is emptied so no backend on the machine counts as installed.
public sealed class DoctorUserMcpTests : IDisposable
{
    private readonly ClaustrumCli cli = new();
    private readonly string emptyPath;

    public DoctorUserMcpTests() => emptyPath = Directory.CreateDirectory(Path.Combine(cli.Home, "empty-path")).FullName;

    public void Dispose() => cli.Dispose();

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    public async Task DoctorPrintsUserMcpWithItsLayerAsync(string value)
    {
        cli.WriteUserConfig($$"""
            {
              "models": { "frontier-coding": "claude:opus", "frontier-reasoning": "claude:opus", "standard-coding": "claude:sonnet", "cheap-coding": "claude:haiku", "fast": "claude:haiku" },
              "roles": { "builder": { "user_mcp": {{value}} } }
            }
            """);

        CliResult result = await cli.RunAsync(["backends", "doctor", "claude"], "", emptyPath);

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.Contains($"  roles.builder.user_mcp = {value}  [User]", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoctorPrintsNoUserMcpLineWhenNoLayerSetsItAsync()
    {
        cli.WriteUserConfig();

        CliResult result = await cli.RunAsync(["backends", "doctor", "claude"], "", emptyPath);

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.DoesNotContain("user_mcp", result.Stdout, StringComparison.Ordinal);
    }
}
