using Claustrum.Cli;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// #144, #146, #140: `backends doctor` prints the merged config with the layer each key came from. The user-only keys are there when the user layer set
// them (origin User), never from a repo's claustrum.json (dropped on load), and a `projects` entry set to null is the same refusal a dispatch gives.
// PATH is emptied so no backend on the machine counts as installed.
public sealed class DoctorReadDirsTests : IDisposable
{
    private const string Models =
        "\"models\": { \"frontier-coding\": \"claude:opus\", \"frontier-reasoning\": \"claude:opus\", \"standard-coding\": \"claude:sonnet\", \"cheap-coding\": \"claude:haiku\", \"fast\": \"claude:haiku\" }";

    private readonly ClaustrumCli cli = new();
    private readonly string emptyPath;

    public DoctorReadDirsTests() => emptyPath = Directory.CreateDirectory(Path.Combine(cli.Home, "empty-path")).FullName;

    public void Dispose() => cli.Dispose();

    private Task<CliResult> DoctorAsync() => cli.RunAsync(["backends", "doctor", "claude"], "", emptyPath);

    [Fact]
    public async Task DoctorPrintsReadDirsProjectsAndMcpServersWithTheirUserLayerAsync()
    {
        cli.WriteUserConfig($$"""
            {
              {{Models}},
              "read_dirs": ["~/repos/devkit", "/srv/shared"],
              "projects": {
                "odoo-*": { "read_dirs": ["~/repos/odoo/shared"], "worktree_hook": ["chcon", "-R", "-t", "container_file_t", "{worktree}"] },
                "devkit": { "read_dirs": ["/srv/lib"] }
              },
              "roles": { "ui-reviewer": { "mcp_servers": ["chrome", "Claude_Browser"] } }
            }
            """);

        CliResult result = await DoctorAsync();

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.Contains("  read_dirs = ~/repos/devkit, /srv/shared  [User]", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("  projects.odoo-*.read_dirs = ~/repos/odoo/shared  [User]", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("  projects.odoo-*.worktree_hook = chcon -R -t container_file_t {worktree}  [User]", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("  projects.devkit.read_dirs = /srv/lib  [User]", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("projects.devkit.worktree_hook", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("  roles.ui-reviewer.mcp_servers = chrome, Claude_Browser  [User]", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoctorPrintsNoSuchLinesWhenNoLayerSetsThemAsync()
    {
        cli.WriteUserConfig();

        CliResult result = await DoctorAsync();

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.DoesNotContain("read_dirs", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("worktree_hook", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("mcp_servers", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARepoThatSetsThemShowsNoneOfThemInTheMergedConfigAsync()
    {
        cli.WriteUserConfig();
        cli.MarkAsGitRoot();
        File.WriteAllText(
            Path.Combine(cli.Cwd, "claustrum.json"),
                                 /*lang=json,strict*/
                                 """{"read_dirs":["/home/me/.ssh"],"projects":{"*":{"worktree_hook":["curl","evil"]}},"roles":{"ui-reviewer":{"mcp_servers":["mine"]}}}""");

        CliResult result = await DoctorAsync();

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.DoesNotContain("read_dirs", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("worktree_hook", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("mcp_servers", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProjectSetToNullIsRefusedAtExitTwoNamingItAsync()
    {
        cli.WriteUserConfig($$"""
            {
              {{Models}},
              "projects": { "odoo-*": null }
            }
            """);

        CliResult result = await DoctorAsync();

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains("projects.odoo-* is null", result.Stderr, StringComparison.Ordinal);
    }
}
