using System.Text.Json;
using Claustrum.Cli;
using Claustrum.Core.Model;

namespace Claustrum.Tests.Cli;

// #145: the one stderr line a front door prints when a receipt lists environment issues. One line however many there are, at most three named, then
// `+K more`; the `where` is the agent's own text, so it is sanitized like every stream line and cut to keep the line one line. Read from a receipt
// off disk leniently: a kind this binary does not know is still printed, a malformed entry is not a crash.
public sealed class EnvironmentWarningTests
{
    private static EnvironmentIssue Issue(EnvironmentIssueKind kind, string? where = null, int count = 1) => new(kind, "what", where, count, "stdout.log:1");

    private static JsonElement Array(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Fact]
    public void NoIssuesNoLine()
    {
        Assert.Null(EnvironmentWarning.Line((EnvironmentIssue[]?)null));
        Assert.Null(EnvironmentWarning.Line([]));
    }

    [Fact]
    public void OneIssueIsNamedByKindCountAndWhere()
    {
        string? line = EnvironmentWarning.Line([Issue(EnvironmentIssueKind.ExternalDirectory, "/srv/shared", count: 18)]);

        Assert.Equal("warning: environment issues (1): external_directory ×18 at /srv/shared. Stop and tell the user before using this result.", line);
    }

    [Fact]
    public void AnIssueWithNoWhereHasNoAtClause()
    {
        Assert.Equal(
            "warning: environment issues (1): plan_mode ×1. Stop and tell the user before using this result.",
            EnvironmentWarning.Line([Issue(EnvironmentIssueKind.PlanMode)]));
    }

    [Fact]
    public void MoreThanThreeIssuesNameThreeThenPlusKMore()
    {
        EnvironmentIssue[] issues =
        [
            Issue(EnvironmentIssueKind.ExternalDirectory, "/a"),
            Issue(EnvironmentIssueKind.Auth, "https://api.github.com/x"),
            Issue(EnvironmentIssueKind.MissingTool),
            Issue(EnvironmentIssueKind.ContainerPermission, "/mnt/extra-addons"),
            Issue(EnvironmentIssueKind.TestGateRed),
        ];

        Assert.Equal(
            "warning: environment issues (5): external_directory ×1 at /a; auth ×1 at https://api.github.com/x; missing_tool ×1; +2 more. Stop and tell the user before using this result.",
            EnvironmentWarning.Line(issues));
    }

    [Fact]
    public void ExactlyThreeIssuesHaveNoMoreSuffix()
    {
        string? line = EnvironmentWarning.Line([Issue(EnvironmentIssueKind.Auth), Issue(EnvironmentIssueKind.MissingTool), Issue(EnvironmentIssueKind.PlanMode)]);

        Assert.DoesNotContain("more", line, StringComparison.Ordinal);
    }

    // The agent's own text: an escape sequence in a command would retitle the terminal, a newline would split the line.
    [Fact]
    public void AWhereIsSanitizedAndCutToEightyCharactersSoTheLineStaysOneLine()
    {
        string where = "docker compose up\n\u001b]0;pwned\u0007 " + new string('x', 200);

        string line = EnvironmentWarning.Line([Issue(EnvironmentIssueKind.ContainerPermission, where)])!;

        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\u001b', line);
        Assert.DoesNotContain('\u0007', line);
        Assert.Contains("docker compose up \\x1b]0;pwned\\x07 ", line, StringComparison.Ordinal);
        Assert.Contains("…", line, StringComparison.Ordinal);
        string shown = line[(line.IndexOf(" at ", StringComparison.Ordinal) + 4)..line.IndexOf(". Stop", StringComparison.Ordinal)];
        Assert.Equal(81, shown.Length);
    }

    // ---- the receipt read off disk ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void AnArrayOfJsonIssuesPrintsLikeTheTypedOnesAndAnUnknownKindIsStillPrinted()
    {
        JsonElement issues = Array(/*lang=json,strict*/ """[{"kind":"external_directory","what":"x","where":"/srv/shared","count":3,"first":"stdout.log:9"},{"kind":"a_kind_from_the_future","count":2}]""");

        Assert.Equal(
            "warning: environment issues (2): external_directory ×3 at /srv/shared; a_kind_from_the_future ×2. Stop and tell the user before using this result.",
            EnvironmentWarning.Line(issues));
    }

    [Fact]
    public void AMalformedEntryDefaultsItsFieldsAndANonObjectEntryIsSkipped()
    {
        JsonElement issues = Array(/*lang=json,strict*/ """[{"kind":7,"count":"many","where":3},5,"x",null,{}]""");

        Assert.Equal(
            "warning: environment issues (2): ? ×1; ? ×1. Stop and tell the user before using this result.",
            EnvironmentWarning.Line(issues));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("\"issues\"")]
    [InlineData("3")]
    public void NullEmptyOrAnythingButANonEmptyArrayPrintsNothing(string json)
    {
        Assert.Null(EnvironmentWarning.Line(Array(json)));
        Assert.Null(EnvironmentWarning.Line((JsonElement?)null));
    }
}
