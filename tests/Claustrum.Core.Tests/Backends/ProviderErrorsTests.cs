using Claustrum.Core.Backends;
using Claustrum.Core.Backends.Opencode;
using Claustrum.Core.Model;

namespace Claustrum.Core.Tests.Backends;

// #126: the classification every backend's parser shares, and the one predicate that says a run did nothing yet.
public sealed class ProviderErrorsTests
{
    [Theory]
    [InlineData(402, ErrorKind.ProviderQuota)]
    [InlineData(429, ErrorKind.ProviderQuota)]
    [InlineData(401, ErrorKind.ProviderAuth)]
    [InlineData(403, ErrorKind.ProviderAuth)]
    [InlineData(404, ErrorKind.ModelNotFound)]
    [InlineData(408, ErrorKind.ProviderTransport)]
    [InlineData(425, ErrorKind.ProviderTransport)]
    [InlineData(502, ErrorKind.ProviderTransport)]
    [InlineData(503, ErrorKind.ProviderTransport)]
    [InlineData(504, ErrorKind.ProviderTransport)]
    [InlineData(529, ErrorKind.ProviderTransport)]
    [InlineData(400, ErrorKind.Other)]
    [InlineData(500, ErrorKind.Other)]
    public void TheHttpStatusAloneDecidesTheKind(int status, ErrorKind expected) => Assert.Equal(expected, ProviderErrors.Classify(status, type: null));

    [Fact]
    public void AStatusWinsOverATypeNameThatSaysSomethingElse() => Assert.Equal(ErrorKind.ProviderQuota, ProviderErrors.Classify(429, "provider.auth"));

    [Theory]
    [InlineData("provider.quota", ErrorKind.ProviderQuota)]
    [InlineData("billing_error", ErrorKind.ProviderQuota)]
    [InlineData("rate_limit_error", ErrorKind.ProviderQuota)]
    [InlineData("insufficient_credits", ErrorKind.ProviderQuota)]
    [InlineData("provider.auth", ErrorKind.ProviderAuth)]
    [InlineData("authentication_error", ErrorKind.ProviderAuth)]
    [InlineData("permission_error", ErrorKind.ProviderAuth)]
    [InlineData("provider.no-route", ErrorKind.ModelNotFound)]
    [InlineData("invalid_model", ErrorKind.ModelNotFound)]
    [InlineData("not_found_error", ErrorKind.ModelNotFound)]
    [InlineData("provider.transport", ErrorKind.ProviderTransport)]
    [InlineData("overloaded_error", ErrorKind.ProviderTransport)]
    [InlineData("service_unavailable", ErrorKind.ProviderTransport)]
    [InlineData("PROVIDER.QUOTA", ErrorKind.ProviderQuota)]
    public void WithoutAStatusTheTypeNameDecidesTheKind(string type, ErrorKind expected) => Assert.Equal(expected, ProviderErrors.Classify(status: null, type));

    // "unknown"/"Agent not found" must not pass for a missing model: only the type is read, never a message.
    [Theory]
    [InlineData(null, null)]
    [InlineData(null, "")]
    [InlineData(null, "unknown")]
    [InlineData(null, "UnknownError")]
    [InlineData(418, "teapot")]
    [InlineData(null, "invalid_request_error")]
    public void AnythingUnplacedIsOther(int? status, string? type) => Assert.Equal(ErrorKind.Other, ProviderErrors.Classify(status, type));

    [Fact]
    public void OnlyOtherIsNotProviderSide()
    {
        Assert.False(ProviderErrors.IsProviderSide(ErrorKind.Other));
        Assert.All(
            [ErrorKind.ProviderQuota, ErrorKind.ProviderAuth, ErrorKind.ModelNotFound, ErrorKind.ProviderTransport],
            kind => Assert.True(ProviderErrors.IsProviderSide(kind)));
    }

    [Theory]
    [InlineData(ErrorKind.ProviderQuota, "provider_quota")]
    [InlineData(ErrorKind.ProviderAuth, "provider_auth")]
    [InlineData(ErrorKind.ModelNotFound, "model_not_found")]
    [InlineData(ErrorKind.ProviderTransport, "provider_transport")]
    [InlineData(ErrorKind.Other, "other")]
    public void NameIsTheReceiptsSnakeCase(ErrorKind kind, string expected) => Assert.Equal(expected, ProviderErrors.Name(kind));

    private static readonly ChangedFile[] oneChange = [new ChangedFile("a.cs", ChangeKind.Modified)];

    [Fact]
    public void BeforeAnyWorkNeedsZeroCompletedCallsNoChangesATreeThatWasReadAndASingleTurn()
    {
        Assert.True(ProviderErrors.BeforeAnyWork(0, [], treeRead: true, firstTurnOnly: true));
        Assert.False(ProviderErrors.BeforeAnyWork(1, [], treeRead: true, firstTurnOnly: true));
        Assert.False(ProviderErrors.BeforeAnyWork(0, oneChange, treeRead: true, firstTurnOnly: true));
        Assert.False(ProviderErrors.BeforeAnyWork(0, [], treeRead: false, firstTurnOnly: true));
        Assert.False(ProviderErrors.BeforeAnyWork(0, [], treeRead: true, firstTurnOnly: false));
    }

    [Fact]
    public void ABackendThatCannotCountItsToolCallsIsNeverBeforeAnyWork() => Assert.False(ProviderErrors.BeforeAnyWork(null, [], treeRead: true, firstTurnOnly: true));

    // #126 (final review, 2026-10-16): "before any work" is bounded at two model requests. The real 402 had exactly two (the second after its errored call).
    [Theory]
    [InlineData(null, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(9, false)]
    public void BeforeAnyWorkIsBoundedAtTwoSteps(int? steps, bool expected) =>
        Assert.Equal(expected, ProviderErrors.BeforeAnyWork(0, [], treeRead: true, firstTurnOnly: true, steps));

    [Fact]
    public void TheStepBoundDoesNotOverrideTheOtherConditions()
    {
        Assert.False(ProviderErrors.BeforeAnyWork(1, [], treeRead: true, firstTurnOnly: true, steps: 1));
        Assert.False(ProviderErrors.BeforeAnyWork(0, oneChange, treeRead: true, firstTurnOnly: true, steps: 1));
        Assert.False(ProviderErrors.BeforeAnyWork(0, [], treeRead: true, firstTurnOnly: false, steps: 1));
    }

    [Fact]
    public void TheRealOpencode402HasTwoStepsSoItIsStillBeforeAnyWork()
    {
        string stdout = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "opencode", "quota-402.jsonl"));
        ParsedOutput parsed = new OpencodeBackend(platform: null!).Parse(stdout, stderr: "", exitCode: 1);

        Assert.Equal(2, parsed.Steps);
        Assert.True(ProviderErrors.BeforeAnyWork(parsed.ToolCallsCompleted, [], treeRead: true, firstTurnOnly: true, parsed.Steps));
    }
}
