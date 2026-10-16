using Claustrum.Core.Backends;
using Claustrum.Core.Backends.Api;
using Claustrum.Core.Model;

namespace Claustrum.Core.Tests.Backends.Api;

// #126: error_kind from the api backend's HTTP bodies. A one-shot call has no tools, so a refusal is always before any work.
public sealed class ApiBackendErrorKindTests
{
    private readonly ApiBackend backend = new(platform: null!);

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "api", name));

    [Fact]
    public void OpenRouterUserNotFoundIsProviderAuthWithNoToolCalls()
    {
        ParsedOutput parsed = backend.Parse(Fixture("error.json"), stderr: "", exitCode: 22);

        Assert.Equal(ErrorKind.ProviderAuth, parsed.ErrorKind);
        Assert.Equal("User not found.", parsed.ErrorDetail);
        Assert.Equal(0, parsed.ToolCallsCompleted);
    }

    // Code 400 and no type: the provider said no, but nothing places it.
    [Fact]
    public void OpenRouterInvalidModelBodyWithCode400IsOther() =>
        Assert.Equal(ErrorKind.Other, backend.Parse(Fixture("error-invalid-model.json"), stderr: "", exitCode: 22).ErrorKind);

    // Anthropic's body names the error by `type`, with no numeric code.
    [Fact]
    public void AnthropicAuthenticationBodyIsProviderAuthByItsType()
    {
        ParsedOutput parsed = backend.Parse(Fixture("anthropic-error.json"), stderr: "", exitCode: 22);

        Assert.Equal(ErrorKind.ProviderAuth, parsed.ErrorKind);
        Assert.Equal("API key is invalid.", parsed.ErrorDetail);
    }

    [Theory]
    [InlineData(/*lang=json,strict*/ """{"error":{"message":"Insufficient credits","code":402}}""", ErrorKind.ProviderQuota)]
    [InlineData(/*lang=json,strict*/ """{"error":{"message":"Rate limited","code":429}}""", ErrorKind.ProviderQuota)]
    [InlineData(/*lang=json,strict*/ """{"error":{"message":"No endpoints found","code":404}}""", ErrorKind.ModelNotFound)]
    [InlineData(/*lang=json,strict*/ """{"error":{"message":"Bad gateway","code":502}}""", ErrorKind.ProviderTransport)]
    [InlineData(/*lang=json,strict*/ """{"type":"error","error":{"type":"rate_limit_error","message":"slow"}}""", ErrorKind.ProviderQuota)]
    [InlineData(/*lang=json,strict*/ """{"type":"error","error":{"type":"overloaded_error","message":"busy"}}""", ErrorKind.ProviderTransport)]
    [InlineData(/*lang=json,strict*/ """{"type":"error","error":{"type":"not_found_error","message":"model: x"}}""", ErrorKind.ModelNotFound)]
    [InlineData(/*lang=json,strict*/ """{"error":{"message":"Something odd","code":418}}""", ErrorKind.Other)]
    public void TheBodysCodeOrTypeDecidesTheKind(string body, ErrorKind expected)
    {
        ParsedOutput parsed = backend.Parse(body, stderr: "", exitCode: 22);

        Assert.True(parsed.IsError);
        Assert.Equal(expected, parsed.ErrorKind);
        Assert.Equal(0, parsed.ToolCallsCompleted);
    }

    // curl's own exit codes for "never got an answer": could not resolve, could not connect, timed out, TLS, receive.
    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(28)]
    [InlineData(35)]
    [InlineData(52)]
    [InlineData(55)]
    [InlineData(56)]
    public void NoBodyAtAllAfterACurlConnectionFailureIsTransport(int curlExit)
    {
        ParsedOutput parsed = backend.Parse("", stderr: "curl: (7) Failed to connect", curlExit);

        Assert.True(parsed.IsError);
        Assert.Equal(ErrorKind.ProviderTransport, parsed.ErrorKind);
        Assert.Contains("Failed to connect", parsed.ErrorDetail, StringComparison.Ordinal);
        Assert.Equal(0, parsed.ToolCallsCompleted);
    }

    [Fact]
    public void NoBodyAfterAnyOtherExitCodeIsOther() =>
        Assert.Equal(ErrorKind.Other, backend.Parse("", stderr: "curl: (3) bad url", exitCode: 3).ErrorKind);

    [Fact]
    public void ASuccessHasNoKind()
    {
        ParsedOutput parsed = backend.Parse(Fixture("openrouter-success.json"), stderr: "", exitCode: 0);

        Assert.False(parsed.IsError);
        Assert.Null(parsed.ErrorKind);
    }
}
