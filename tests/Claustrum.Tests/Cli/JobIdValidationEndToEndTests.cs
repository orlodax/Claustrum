using Claustrum.Cli;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// #124 stage 2 review F6: every verb that takes a job id checks its shape before the id becomes a path. A malformed id is a usage
// error (exit 2) whatever the verb, and a well-formed unknown one is too; nothing is created for either.
public sealed class JobIdValidationEndToEndTests : IDisposable
{
    private readonly ClaustrumCli cli = ClaustrumCli.Initialized();

    public void Dispose() => cli.Dispose();

    private static readonly string[] verbs = ["say", "wait", "follow", "reply", "cancel", "host", "show", "logs"];

    private static readonly string[] malformedIds = ["../x", "C:x", "", "abc", "20261015-190000-AAAAAAAA", "20261015-190000-aaaaaaa", "20261015-190000-aaaaaaaa/.."];

    public static TheoryData<string, string> Cases()
    {
        TheoryData<string, string> data = [];
        foreach (string verb in verbs)
        {
            foreach (string id in malformedIds)
            {
                // Follow's empty id has its own test (EmptyIdForFollow): only an omitted id means "every new job" there.
                if (verb != "follow" || id.Length > 0)
                    data.Add(verb, id);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EveryJobsVerbRefusesAMalformedIdWithExitTwoAsync(string verb, string id)
    {
        string[] extra = verb is "say" or "reply" ? ["hello"] : [];

        CliResult process = await cli.RunAsync(["jobs", verb, id, .. extra], "", IsolatedRepo.ChildPath());

        Assert.Equal(ExitCodes.Usage, process.ExitCode);
        Assert.Empty(process.Stdout.Trim());
        Assert.False(Directory.Exists(cli.JobsDirectory), "a refused id creates nothing");
    }

    [Theory]
    [InlineData("say")]
    [InlineData("wait")]
    [InlineData("follow")]
    [InlineData("reply")]
    [InlineData("cancel")]
    [InlineData("host")]
    public async Task AWellFormedIdOfNoJobIsAUsageErrorTooAsync(string verb)
    {
        string[] extra = verb is "say" or "reply" ? ["hello"] : [];

        CliResult process = await cli.RunAsync(["jobs", verb, "20261015-000000-deadbeef", .. extra], "", IsolatedRepo.ChildPath());

        Assert.Equal(ExitCodes.Usage, process.ExitCode);
    }

    // The brief says every verb refuses an empty id with exit 2; `jobs follow` takes an optional id ("omit to follow every new job"),
    // and `jobs follow ""` is read as omitted (JobsFollowCommand.ExecuteAsync tests `jobId is { Length: > 0 }`), so it follows for ever.
    // Decided 2026-10-15: an id given empty is malformed (exit 2); only an omitted id follows every new job.
    [Fact]
    public async Task EmptyIdForFollowIsRefusedWithExitTwoAsync()
    {
        CliResult process = await cli.RunAsync(["jobs", "follow", ""], "", IsolatedRepo.ChildPath());

        Assert.Equal(ExitCodes.Usage, process.ExitCode);
    }
}
