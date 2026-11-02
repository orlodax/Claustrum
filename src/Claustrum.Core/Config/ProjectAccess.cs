using System.IO.Enumeration;
using Claustrum.Core.Backends;
using Claustrum.Core.Git;
using Claustrum.Core.Model;
using Claustrum.Core.Platform;

namespace Claustrum.Core.Config;

/// <summary>
/// What the user config grants a job dispatched from a directory (#144, #146): the configured read dirs (global <c>read_dirs</c> plus
/// those of every <c>projects.&lt;glob&gt;</c> matching <see cref="Project"/>, the main checkout's name), absolute, existing and deduped;
/// the checkout's <see cref="GitCommonDir"/>; the matching projects' worktree hooks, in config order. What was dropped is said in
/// <see cref="Warnings"/> and <see cref="Issues"/>. NOTES.md "Read dirs and the worktree hook".
/// </summary>
public sealed record ProjectAccess(string? Project, string[] ConfiguredDirs, string? GitCommonDir, string[][] WorktreeHooks, string[] Warnings, EnvironmentIssue[] Issues)
{
    /// <summary>Replaced, in every argument of a <c>worktree_hook</c>, by the worktree's absolute path.</summary>
    public const string WorktreeToken = "{worktree}";

    private const string Dispatch = "dispatch";
    private const int MaxWhere = 5;

    /// <summary>
    /// Resolves <paramref name="merged"/> (whose <c>read_dirs</c> and <c>projects</c> only the user layer can have set) for <paramref name="cwd"/>.
    /// Every entry is checked, the matching projects' and the others': a relative path or an empty hook is a <see cref="ConfigException"/>
    /// naming its key, whatever directory the job is dispatched from.
    /// </summary>
    public static ProjectAccess Resolve(ConfigDocument merged, string userConfigPath, string cwd, IPlatform platform)
    {
        GitCheckout? checkout = GitCheckout.Find(cwd, platform);
        string? project = checkout is null ? null : Path.GetFileName(checkout.MainCheckout);
        bool windows = platform.Os == ClaustrumOs.Windows;

        List<string> configured = [.. Absolute("read_dirs", merged.ReadDirs, userConfigPath, platform)];
        List<string[]> hooks = [];
        foreach (KeyValuePair<string, ProjectSettings> entry in merged.Projects ?? [])
        {
            string[] dirs = Absolute($"projects.{entry.Key}.read_dirs", entry.Value.ReadDirs, userConfigPath, platform);
            string[]? hook = Hook($"projects.{entry.Key}.worktree_hook", entry.Value.WorktreeHook, userConfigPath);
            if (project is null || !FileSystemName.MatchesSimpleExpression(entry.Key, project, ignoreCase: windows))
                continue;

            configured.AddRange(dirs);
            if (hook is not null)
                hooks.Add(hook);
        }

        string root = Normalize(Path.GetFullPath(cwd));
        List<string> kept = [];
        List<string> missing = [];
        List<string> warnings = [];
        foreach (string dir in configured.Distinct(PathComparer))
        {
            if (!platform.PathExists(dir) || platform.FileExists(dir))
            {
                missing.Add(dir);
                warnings.Add($"read_dirs: {dir} {(platform.FileExists(dir) ? "is not a directory" : "does not exist")}; the job runs without it");
            }
            else if (Contains(dir, root))
            {
                // Read-only around the job's own tree would forbid its edits (claude's Edit deny matches the tree too), and inside it adds nothing.
                warnings.Add($"read_dirs: {dir} holds the working directory {root}; not given to the job, which could not edit its own tree under it");
            }
            else if (!Contains(root, dir))
            {
                kept.Add(dir);
            }
        }

        EnvironmentIssue[] issues = missing.Count == 0
            ? []
            : [new EnvironmentIssue(EnvironmentIssueKind.MissingDirectory, "a configured read_dirs directory does not exist", Where(missing), missing.Count, Dispatch)];
        string? commonDir = checkout is not null && platform.PathExists(checkout.CommonDir) ? checkout.CommonDir : null;
        return new ProjectAccess(project, [.. kept], commonDir, [.. hooks], [.. warnings], issues);
    }

    /// <summary>
    /// What a run whose working directory is <paramref name="runCwd"/> may read: <see cref="ConfiguredDirs"/>, plus the git common dir when it is
    /// not inside <paramref name="runCwd"/> (a linked worktree's, a job worktree's), so git's own metadata is reachable there too.
    /// </summary>
    public string[] ReadDirsFor(string runCwd) =>
        GitCommonDir is { } common && !Contains(Normalize(Path.GetFullPath(runCwd)), common) && !ConfiguredDirs.Contains(common, PathComparer)
            ? [.. ConfiguredDirs, common]
            : ConfiguredDirs;

    /// <summary>
    /// What <paramref name="backend"/> does with <see cref="ConfiguredDirs"/> at <paramref name="level"/>, said at dispatch: nothing at all
    /// (<see cref="IBackend.HonoursReadDirs"/> false), or read-write (<see cref="IBackend.ReadDirsWritableAt"/>). The git common dir alone
    /// says nothing: the api backend has no file tools to read it with, and every other one reads it.
    /// </summary>
    public (string[] Warnings, EnvironmentIssue[] Issues) ForBackend(IBackend backend, PermissionLevel level)
    {
        if (ConfiguredDirs.Length == 0)
            return ([], []);

        string dirs = Where(ConfiguredDirs);
        if (!backend.HonoursReadDirs)
        {
            return ([$"read_dirs: the {backend.Name} backend cannot give a job extra directories to read; {dirs} stay out of its reach"],
                [new EnvironmentIssue(EnvironmentIssueKind.ReadDirsUnsupported, $"the {backend.Name} backend cannot honour read_dirs", dirs, ConfiguredDirs.Length, Dispatch)]);
        }

        return backend.ReadDirsWritableAt(level)
            ? ([$"read_dirs: {backend.Name} grants read_dirs read-write at level {PermissionLevelParser.ToConfigString(level)}: {dirs}"], [])
            : ([], []);
    }

    private static string[] Absolute(string key, string[]? entries, string userConfigPath, IPlatform platform)
    {
        if (entries is null)
            return [];

        string[] resolved = new string[entries.Length];
        for (int index = 0; index < entries.Length; index++)
        {
            // A JSON null in the array arrives as a null element whatever the annotation says.
            string raw = entries[index]?.Trim() ?? "";
            string expanded = raw == "~" ? platform.HomeDirectory
                : raw.StartsWith("~/", StringComparison.Ordinal) || raw.StartsWith("~\\", StringComparison.Ordinal) ? Path.Combine(platform.HomeDirectory, raw[2..])
                : raw;
            if (expanded.Length == 0 || !Path.IsPathFullyQualified(expanded))
                throw new ConfigException($"{userConfigPath}: {key}[{index}] '{entries[index]}' is not an absolute path; write it from the root or from ~/, since a relative one would depend on where a job runs");

            resolved[index] = Normalize(Path.GetFullPath(expanded));
        }

        return resolved;
    }

    private static string[]? Hook(string key, string[]? argv, string userConfigPath) =>
        argv is null ? null
        : argv.Length == 0 || string.IsNullOrWhiteSpace(argv[0])
            ? throw new ConfigException($"{userConfigPath}: {key} names no program; give the command as an argv array, the program first, e.g. [\"chcon\", \"-R\", \"-t\", \"container_file_t\", \"{WorktreeToken}\"]")
            : argv;

    // Paths are the OS's own, so they compare the way its file system does; only the project glob follows IPlatform.Os.
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    // `outer` is `inner` or one of its ancestors.
    private static bool Contains(string outer, string inner)
    {
        if (inner.Equals(outer, PathComparison))
            return true;

        string prefix = Path.EndsInDirectorySeparator(outer) ? outer : outer + Path.DirectorySeparatorChar;
        return inner.StartsWith(prefix, PathComparison);
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(path);

    // #145's shape for `where`: the distinct locations, at most five, then how many more.
    private static string Where(IReadOnlyList<string> locations) =>
        locations.Count <= MaxWhere ? string.Join(", ", locations) : $"{string.Join(", ", locations.Take(MaxWhere))} +{locations.Count - MaxWhere} more";
}
