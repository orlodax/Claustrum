using System.Diagnostics;
using Claustrum.Core.Model;
using Claustrum.Core.Tracing;

namespace Claustrum.Core.Tests.Tracing;

// #145: the classifier reads text a model wrote, so no string may make it throw or stall: a throw costs the receipt its issues (Runner turns it into a
// warning), a stall holds the run's result back. A seeded fuzz over the characters a shell line is built from, and the shapes that grow with length.
public sealed class EnvironmentClassifierRobustnessTests
{
    private const string Output = "Traceback (most recent call last):\nPermissionError: [Errno 13] Permission denied: '/mnt/x'\nHTTP 404: Not Found (https://api.github.com/x)\nfatal: Authentication failed for 'https://github.com/x'";

    private static RunTrace TraceOf(string command, string output) =>
        new([new TracedCall("Bash", ToolFamily.Shell, command, "/work/repo", null, false, null, output, 3, 0)], []);

    private static EnvironmentIssue[] Classify(string command) =>
        EnvironmentClassifier.Classify(TraceOf(command, Output), new PermissionPolicy(PermissionLevel.EditShell, []), null, "/work/repo", []);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RandomShellLikeTextNeverThrows(int seed)
    {
        const string alphabet = "ab $()`'\"\\|&;<>-#{}!\n\t=*?~/.0123456789cat gh git curl docker tee EOF";
        Random random = new(seed);

        for (int iteration = 0; iteration < 3000; iteration++)
        {
            char[] text = new char[random.Next(0, 160)];
            for (int index = 0; index < text.Length; index++)
                text[index] = alphabet[random.Next(alphabet.Length)];

            string command = new(text);
            Exception? thrown = Record.Exception(() => Classify(command));
            Assert.True(thrown is null, $"{thrown?.GetType().Name} on {System.Text.Json.JsonSerializer.Serialize(command)}");
        }
    }

    [Fact]
    public void ShapesThatGrowWithLengthStayFast()
    {
        string[] commands =
        [
            string.Concat(Enumerable.Repeat("cat a && ", 2000)) + "ls",
            string.Concat(Enumerable.Repeat("$(", 500)) + "x" + new string(')', 500),
            string.Concat(Enumerable.Repeat("echo $(cat f) ", 2000)),
            string.Concat(Enumerable.Repeat("cat <<E", 500)) + "\nbody\nE\nE\nE",
            "echo \"" + new string('a', 100_000),
            string.Concat(Enumerable.Repeat("echo `cat f` ", 2000)),
            new string('(', 2000) + "cat f" + new string(')', 2000),
            string.Concat(Enumerable.Repeat("a=1 ", 5000)) + "cat f",
            string.Concat(Enumerable.Repeat("> /tmp/x ", 5000)) + "ls",
        ];

        Stopwatch clock = Stopwatch.StartNew();
        foreach (string command in commands)
            Assert.Null(Record.Exception(() => Classify(command)));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");
    }

    [Fact]
    public void AHugeOutputIsReadOnceAndDoesNotStall()
    {
        string output = string.Concat(Enumerable.Repeat("ordinary line of build output with no trouble in it\n", 200_000)) + "PermissionError: [Errno 13] Permission denied: '/mnt/x'";
        Stopwatch clock = Stopwatch.StartNew();

        EnvironmentIssue[] issues = EnvironmentClassifier.Classify(TraceOf("docker compose up odoo", output), new PermissionPolicy(PermissionLevel.EditShell, []), null, "/work/repo", []);

        Assert.Equal(EnvironmentIssueKind.ContainerPermission, Assert.Single(issues).Kind);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");
    }

    [Fact]
    public void ACallWithNoCommandNoOutputAndNoPathIsNotAnIssueAndNotACrash()
    {
        RunTrace trace = new(
        [
            new TracedCall("Bash", ToolFamily.Shell, null, null, null, false, null, null, 1),
            new TracedCall("Bash", ToolFamily.Shell, "", null, null, true, "", "", 2, 1),
            new TracedCall("Read", ToolFamily.Read, null, null, null, true, "No such file or directory", null, 3),
            new TracedCall("WebFetch", ToolFamily.Fetch, null, null, null, true, "status code 403", null, 4),
            new TracedCall("", ToolFamily.Other, null, null, null, true, "Permission denied: external_directory", null, 5),
        ],
        []);

        EnvironmentIssue[] issues = EnvironmentClassifier.Classify(trace, new PermissionPolicy(PermissionLevel.EditShell, []), null, "/work/repo", []);

        Assert.Equal([EnvironmentIssueKind.Auth, EnvironmentIssueKind.ExternalDirectory], issues.Select(issue => issue.Kind));
    }
}
