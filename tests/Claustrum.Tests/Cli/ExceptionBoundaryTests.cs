using Claustrum.Casts;
using Claustrum.Cli;
using Claustrum.Core;
using Claustrum.Core.Config;
using Claustrum.Core.Process;
using Claustrum.Roles;

namespace Claustrum.Tests.Cli;

// docs/PLAN.md §A5's exit-code table is a contract callers script against, and two of the eleven
// review findings were stated as "...now exits 2 naming the file" — but only the exception type was
// ever asserted, never the code the process actually returns for it. This is that half.
public sealed class ExceptionBoundaryTests
{
    [Theory]
    [MemberData(nameof(UsageExceptions))]
    public void DomainExceptionsAreUsageErrors(Exception exception)
    {
        Assert.Equal(ExitCodes.Usage, ExceptionBoundary.Handle(exception));
    }

    public static TheoryData<Exception> UsageExceptions() =>
    [
        new ConfigException("bad config"),
        new RoleRenderException("'.vscode/mcp.json' is not valid JSON"),
        new CliUsageException("use either --check or --dry-run, not both"),
        new BlindGateException("blind role: brief carries rationale"),
        new RunRequestException("--timeout must be greater than zero"),
        new CastException("'answers.json': bad JSON"),
        new ArgumentException("bad argument"),
    ];

    [Fact]
    public void ABackendMissingFromPathGetsItsOwnExitCode()
    {
        Assert.Equal(ExitCodes.BackendMissing, ExceptionBoundary.Handle(new BackendNotFoundException("claude")));
    }

    [Fact]
    public void AnythingElseIsABackendFailure()
    {
        Assert.Equal(ExitCodes.BackendFailure, ExceptionBoundary.Handle(new InvalidOperationException("boom")));
    }
}
