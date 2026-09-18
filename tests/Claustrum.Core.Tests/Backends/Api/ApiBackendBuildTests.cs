using System.Text.Json;
using Claustrum.Core.Backends;
using Claustrum.Core.Backends.Api;
using Claustrum.Core.Model;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests.Backends.Api;

// Build writes two real temp files (request body + curl config) under run.JobDirectory and points
// curl at them instead of putting the API key/brief on argv — these tests use a real temp directory
// so File.WriteAllText/ReadAllText round-trip for real, the way ProcessSpec.TempFiles cleanup expects.
public sealed class ApiBackendBuildTests : IDisposable
{
    private readonly string jobDirectory = Directory.CreateTempSubdirectory("claustrum-api-build-").FullName;
    private readonly FakePlatform platform = new();

    public void Dispose() => Directory.Delete(jobDirectory, recursive: true);

    private ApiBackend Backend() => new(platform);

    private ResolvedRun MakeRun(string model) => new(
        Role: new ResolvedRole("code-reviewer", "you are a reviewer", "api", model, "high", new PermissionPolicy(PermissionLevel.ReadOnly, []), Blind: true, HasReport: true),
        Brief: "review this diff",
        Cwd: "/repo",
        BudgetUsd: null,
        ResumeSession: null,
        AttachFiles: [],
        Stream: false,
        SystemPromptFilePath: WriteSystemPrompt(),
        JobDirectory: jobDirectory,
        Env: []);

    private string WriteSystemPrompt()
    {
        string path = Path.Combine(jobDirectory, "system.md");
        File.WriteAllText(path, "you are a reviewer");
        return path;
    }

    [Fact]
    public void OpenRouterModelPostsToOpenRouterWithBearerAuth()
    {
        platform.EnvironmentVariables["OPENROUTER_API_KEY"] = "sk-or-test-123";

        ProcessSpec spec = Backend().Build(MakeRun("openrouter:deepseek/deepseek-v4-pro"));

        Assert.Equal("curl", spec.Exe);
        Assert.Equal("https://openrouter.ai/api/v1/chat/completions", spec.Args[^1]);
        Assert.Equal(2, spec.TempFiles.Length);
        string config = File.ReadAllText(spec.TempFiles.Single(f => f.EndsWith("api-curl-config", StringComparison.Ordinal)));
        Assert.Contains("Authorization: Bearer sk-or-test-123", config, StringComparison.Ordinal);

        string body = File.ReadAllText(spec.TempFiles.Single(f => f.EndsWith("api-request-body.json", StringComparison.Ordinal)));
        using JsonDocument document = JsonDocument.Parse(body);
        Assert.Equal("deepseek/deepseek-v4-pro", document.RootElement.GetProperty("model").GetString());
        Assert.Equal("review this diff", document.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
    }

    [Fact]
    public void AnthropicModelPostsToAnthropicWithXApiKeyAndMaxTokens()
    {
        platform.EnvironmentVariables["ANTHROPIC_API_KEY"] = "sk-ant-test-456";

        ProcessSpec spec = Backend().Build(MakeRun("anthropic:claude-opus-4-5"));

        Assert.Equal("https://api.anthropic.com/v1/messages", spec.Args[^1]);
        string config = File.ReadAllText(spec.TempFiles.Single(f => f.EndsWith("api-curl-config", StringComparison.Ordinal)));
        Assert.Contains("x-api-key: sk-ant-test-456", config, StringComparison.Ordinal);
        Assert.Contains("anthropic-version:", config, StringComparison.Ordinal);

        string body = File.ReadAllText(spec.TempFiles.Single(f => f.EndsWith("api-request-body.json", StringComparison.Ordinal)));
        using JsonDocument document = JsonDocument.Parse(body);
        Assert.Equal("claude-opus-4-5", document.RootElement.GetProperty("model").GetString());
        Assert.True(document.RootElement.GetProperty("max_tokens").GetInt32() > 0);
        Assert.Equal("you are a reviewer", document.RootElement.GetProperty("system").GetString());
    }

    [Fact]
    public void MissingApiKeyThrowsBeforeSpawning()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => Backend().Build(MakeRun("openrouter:deepseek/deepseek-v4-pro")));

        Assert.Contains("OPENROUTER_API_KEY", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownProviderThrows()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => Backend().Build(MakeRun("groq:llama-huge")));

        Assert.Contains("groq", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ModelWithNoProviderPrefixThrows()
    {
        Assert.Throws<InvalidOperationException>(() => Backend().Build(MakeRun("deepseek-v4-pro")));
    }

    [Fact]
    public void RequestBodyUsesTheJobsSystemPromptFile()
    {
        platform.EnvironmentVariables["OPENROUTER_API_KEY"] = "sk-or-test-123";

        ProcessSpec spec = Backend().Build(MakeRun("openrouter:deepseek/deepseek-v4-pro"));

        string body = File.ReadAllText(spec.TempFiles.Single(f => f.EndsWith("api-request-body.json", StringComparison.Ordinal)));
        using JsonDocument document = JsonDocument.Parse(body);
        Assert.Equal("you are a reviewer", document.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }
}
