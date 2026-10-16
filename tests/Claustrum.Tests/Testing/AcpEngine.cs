using System.Text.Json;

namespace Claustrum.Tests.Testing;

/// <summary>What the ACP engine tests (AcpLiveEngineTests and its siblings) read off a <see cref="LiveJobHarness.ForAcp"/> job.</summary>
public static class AcpEngine
{
    public static async Task WaitForPromptsAsync(LiveJobHarness job, int prompts) =>
        await LiveJobHarness.WaitUntilAsync(() => job.Acp.Prompts >= prompts, $"prompt {prompts} to reach the agent");

    /// <summary>The frames the client sent, as the job's own acp-client.jsonl holds them.</summary>
    public static string[] ClientFrames(LiveJobHarness job) => File.Exists(job.Job.AcpClientLog) ? File.ReadAllLines(job.Job.AcpClientLog) : [];

    public static string MethodOf(string frame)
    {
        using JsonDocument document = JsonDocument.Parse(frame);
        return document.RootElement.TryGetProperty("method", out JsonElement method) ? method.GetString() ?? "" : "(response)";
    }

    public static string[] ClientMethods(LiveJobHarness job) => [.. ClientFrames(job).Select(MethodOf)];

    public static string IdOf(string frame)
    {
        using JsonDocument document = JsonDocument.Parse(frame);
        return document.RootElement.GetProperty("id").ToString();
    }

    /// <summary>The agent's shell and the `sleep` it last waited on are both gone: a stop kills the tree, not just the process it started.</summary>
    public static async Task AssertNoOrphanAsync(LiveJobHarness job)
    {
        int shell = job.Acp.Pid(1);
        int? sleep = job.Acp.NapPid;
        await LiveJobHarness.WaitUntilAsync(() => !FakeAcpAgent.IsRunning(shell) && (sleep is null || !FakeAcpAgent.IsRunning(sleep.Value)), "the agent and its sleep to be gone");
    }
}
