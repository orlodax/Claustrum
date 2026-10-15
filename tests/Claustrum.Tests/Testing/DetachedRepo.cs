using System.Diagnostics;
using System.Text.Json;
using Claustrum.Live;

namespace Claustrum.Tests.Testing;

/// <summary>
/// An <see cref="IsolatedRepo"/> for the detached-host tests (#124 stage 2). A `claustrum jobs host` outlives the `run` that started it,
/// so teardown is what keeps the machine clean: <see cref="Dispose"/> kills every host this home recorded (host.json), with the fake
/// claude under it, and every fake tree root, before the directories go. POSIX only: the conversing fake is a shell script.
/// </summary>
public sealed class DetachedRepo : IDisposable
{
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, Process> roots = [];

    /// <param name="forCoordinate">The repository `coordinate` needs (machinery rules ignored, a spawned-architect cast committed) instead of a plain one with a builder cast.</param>
    public DetachedRepo(bool forCoordinate = false)
    {
        FakeClaude.RequirePosixShell();
        if (forCoordinate)
        {
            Repo = IsolatedRepo.ForCoordinate();
            return;
        }

        Repo = new IsolatedRepo();
        Repo.WriteCast(maxParallel: null);
    }

    public IsolatedRepo Repo { get; }

    public string JobsRoot => Repo.JobsRoot;

    public string JobDir(string id) => Path.Combine(JobsRoot, id);

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The environment of a child of a coordinate job: its tree, and whether that job's architect holds a live session.</summary>
    public static Dictionary<string, string?> Tree(string treeId, string? architectLive = "1") => new()
    {
        ["CLAUSTRUM_PARENT_JOB"] = treeId,
        ["CLAUSTRUM_ARCHITECT_LIVE"] = architectLive,
    };

    public Task<CliResult> RunBuilderAsync(IReadOnlyDictionary<string, string?> env, params string[] extraArgs) =>
        Repo.RunWithEnvAsync(["run", "builder", "--brief", "do the task", .. extraArgs], env);

    /// <summary>Any command in the repository, in a child with this tree's environment (<see cref="Tree"/>).</summary>
    public Task<CliResult> RunWithTreeAsync(IReadOnlyDictionary<string, string?> env, params string[] args) => Repo.RunWithEnvAsync(args, env);

    public RunningCli StartBuilder(IReadOnlyDictionary<string, string?> env, params string[] extraArgs) =>
        Repo.StartCliWithEnv(env, ["run", "builder", "--brief", "do the task", .. extraArgs]);

    public string[] JobIds() => Repo.JobDirectories();

    public string OnlyJob() => Assert.Single(JobIds(), id => !IsRoot(id));

    private bool IsRoot(string id) => File.Exists(Path.Combine(JobDir(id), "owner.json")) && !File.Exists(Path.Combine(JobDir(id), "host.json"));

    public JsonElement? Json(string id, string file)
    {
        string path = Path.Combine(JobDir(id), file);
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using StreamReader reader = new(stream);
            using JsonDocument document = JsonDocument.Parse(reader.ReadToEnd());
            return document.RootElement.Clone();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public string? LiveState(string id) => Json(id, "live.json")?.GetProperty("state").GetString();

    public string? ResultStatus(string id) => Json(id, "result.json")?.GetProperty("status").GetString();

    public string Conversation(string id) =>
        File.Exists(Path.Combine(JobDir(id), "conversation.jsonl")) ? ReadShared(Path.Combine(JobDir(id), "conversation.jsonl")) : "";

    private static string ReadShared(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }

    /// <summary>The recorded host of a job, or null.</summary>
    public HostRecord? Host(string id)
    {
        string path = Path.Combine(JobDir(id), "host.json");
        return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), LiveJsonContext.Default.HostRecord) : null;
    }

    public bool HostAlive(string id) => Host(id)?.IsAlive() ?? false;

    /// <summary>Waits for a single job directory to hold a file, and returns its id.</summary>
    public async Task<string> WaitForJobAsync(Func<string, bool> ready, string what)
    {
        string? found = null;
        await WaitUntilAsync(() => (found = JobIds().Where(id => !IsRoot(id)).FirstOrDefault(ready)) is not null, what);
        return found ?? throw new InvalidOperationException(what);
    }

    public static async Task WaitUntilAsync(Func<bool> condition, string what, TimeSpan? limit = null)
    {
        DateTime due = DateTime.UtcNow + (limit ?? Limit);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < due, $"timed out waiting for {what}");
            await Task.Delay(50, Ct);
        }
    }

    public Task WaitForQuestionAsync(string id) => WaitUntilAsync(() => LiveState(id) == "waiting_for_input" && Conversation(id).Contains("\"kind\":\"question\"", StringComparison.Ordinal), $"job {id} to wait for input");

    public Task WaitForResultAsync(string id, TimeSpan? limit = null) => WaitUntilAsync(() => ResultStatus(id) is not null, $"job {id} to write its receipt", limit);

    public Task WaitForHostGoneAsync(string id) => WaitUntilAsync(() => !HostAlive(id), $"the host of {id} to exit");

    /// <summary>The kernel's session id of a process (Linux), or null where /proc does not say.</summary>
    public static int? SessionOf(int pid)
    {
        string stat = $"/proc/{pid}/stat";
        if (!File.Exists(stat))
            return null;

        // pid (comm) state ppid pgrp session …: comm may hold spaces, so split after its closing parenthesis.
        string text = File.ReadAllText(stat);
        string[] fields = text[(text.LastIndexOf(')') + 2)..].Split(' ');
        return int.Parse(fields[3], System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Reads these files of every job directory in a tight loop until <paramref name="stop"/>, and returns each version it caught that does not
    /// parse as JSON. An atomic write (temp file, then rename) leaves none: not a half-written file, and not an empty one under the target's name.
    /// </summary>
    public Task<string[]> WatchForTornFilesAsync(CancellationToken stop, params string[] files) => Task.Run(
        () =>
        {
            List<string> torn = [];
            while (!stop.IsCancellationRequested)
            {
                foreach (string id in JobIds())
                {
                    foreach (string file in files)
                    {
                        if (ReadOrNull(Path.Combine(JobDir(id), file)) is { } text && !Parses(text))
                            torn.Add($"{id}/{file}: '{text}'");
                    }
                }
            }

            return torn.ToArray();
        },
        CancellationToken.None);

    // Null while the file does not exist.
    private static string? ReadOrNull(string path)
    {
        try
        {
            return ReadShared(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool Parses(string text)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Kills the host of a job and everything under it (the fake claude), as a crash would.</summary>
    public void KillHost(string id)
    {
        if (Host(id) is { } host)
            KillTree(host.Pid);
    }

    private static void KillTree(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // Already gone.
        }
    }

    /// <summary>
    /// A job directory standing in for a coordinate job (the root of a tree), whose owner is a `sleep` this fixture started: killing
    /// the sleep is the coordinate process dying. <paramref name="withOwner"/> false leaves no owner.json, as a tree id exported by hand has none.
    /// </summary>
    public string MintRoot(bool withOwner = true)
    {
        string id = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}";
        Directory.CreateDirectory(JobDir(id));
        if (!withOwner)
            return id;

        ProcessStartInfo startInfo = new() { FileName = "/bin/sleep", UseShellExecute = false, CreateNoWindow = true };
        startInfo.ArgumentList.Add("600");
        Process owner = Process.Start(startInfo) ?? throw new InvalidOperationException("sleep failed to start");
        roots[id] = owner;
        OwnerRecord record = new(owner.Id, new DateTimeOffset(owner.StartTime));
        File.WriteAllText(Path.Combine(JobDir(id), "owner.json"), JsonSerializer.Serialize(record, LiveJsonContext.Default.OwnerRecord));
        return id;
    }

    /// <summary>The coordinate process of a fake root dying (kill -9).</summary>
    public void KillRootOwner(string rootId)
    {
        roots[rootId].Kill(entireProcessTree: true);
        roots[rootId].WaitForExit(5000);
    }

    public void WriteRootReceipt(string rootId) =>
        File.WriteAllText(Path.Combine(JobDir(rootId), "result.json"), /*lang=json,strict*/ """{"status":"success"}""");

    public void Dispose()
    {
        foreach (string id in JobIds())
            KillHost(id);

        foreach (Process root in roots.Values)
        {
            try
            {
                if (!root.HasExited)
                    root.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }

            root.Dispose();
        }

        Repo.Dispose();
    }
}
