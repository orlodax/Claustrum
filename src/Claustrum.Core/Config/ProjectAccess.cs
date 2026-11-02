using System.IO.Enumeration;
using Claustrum.Core.Backends;
using Claustrum.Core.Git;
using Claustrum.Core.Model;
using Claustrum.Core.Platform;

namespace Claustrum.Core.Config;

/// <summary>
/// What the user config grants a job dispatched from a directory (#144, #146): the configured read dirs (global <c>read_dirs</c> plus
/// those of every <c>projects.&lt;glob&gt;</c> matching <see cref="Project"/>, the main checkout's name), absolute, existing and deduped;
/// the checkout's <see cref="TopLevel"/> and <see cref="GitCommonDir"/>; the matching projects' worktree hooks, in config order. What was
/// dropped is said in <see cref="Warnings"/> and <see cref="Issues"/>. NOTES.md "Read dirs and the worktree hook".
/// </summary>
public sealed record ProjectAccess(
    string? Project, string[] ConfiguredDirs, string? TopLevel, string? GitCommonDir, string[][] WorktreeHooks, string[] Warnings, EnvironmentIssue[] Issues)
{
    /// <summary>Replaced, in every argument of a <c>worktree_hook</c>, by the worktree's absolute path.</summary>
    public const string WorktreeToken = "{worktree}";

    private const string Dispatch = "dispatch";
    private const int MaxWhere = 5;

    /// <summary>
    /// Resolves <paramref name="merged"/> (whose <c>read_dirs</c> and <c>projects</c> only the user layer can have set) for <paramref name="cwd"/>.
    /// Every entry is checked, the matching projects' and the others': a relative path, a null project or a hook with no program (or a
    /// relative one) is a <see cref="ConfigException"/> naming its key, whatever directory the job is dispatched from.
    /// </summary>
    public static ProjectAccess Resolve(ConfigDocument merged, string userConfigPath, string cwd, IPlatform platform)
    {
        GitCheckout? checkout = GitCheckout.Find(cwd, platform);
        string? project = checkout?.ProjectName;
        bool windows = platform.Os == ClaustrumOs.Windows;

        List<string> configured = [.. Absolute("read_dirs", merged.ReadDirs, userConfigPath, platform)];
        List<string[]> hooks = [];
        foreach (KeyValuePair<string, ProjectSettings> entry in merged.Projects ?? [])
        {
            // `"projects": {"x": null}` arrives as a null value whatever the annotation says.
            ProjectSettings settings = entry.Value ?? throw NullProject(userConfigPath, entry.Key);
            string[] dirs = Absolute($"projects.{entry.Key}.read_dirs", settings.ReadDirs, userConfigPath, platform);
            string[]? hook = Hook($"projects.{entry.Key}.worktree_hook", settings.WorktreeHook, userConfigPath);
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
        return new ProjectAccess(project, [.. kept], checkout?.TopLevel, commonDir, [.. hooks], [.. warnings], issues);
    }

    /// <summary>The <see cref="ConfigException"/> for a <c>projects</c> entry that is JSON null, also raised by <c>backends doctor</c>.</summary>
    public static ConfigException NullProject(string userConfigPath, string glob) =>
        new($"{userConfigPath}: projects.{glob} is null; give it an object, e.g. {{\"read_dirs\": [\"~/repos/shared\"]}}, or remove it");

    /// <summary>
    /// What a run whose git top level is <paramref name="runTopLevel"/> may read: <see cref="ConfiguredDirs"/>, plus the git common dir when
    /// <paramref name="withGitCommonDir"/> and it lies outside that tree without holding it (a linked worktree's, a job worktree's; never a
    /// main checkout's own `.git`, nor a bare repo that holds its worktrees). A caller whose backend would make it writable passes false.
    /// </summary>
    public string[] ReadDirsFor(string runTopLevel, bool withGitCommonDir)
    {
        string top = Normalize(Path.GetFullPath(runTopLevel));
        return withGitCommonDir && GitCommonDir is { } common && !Contains(top, common) && !Contains(common, top) && !ConfiguredDirs.Contains(common, PathComparer)
            ? [.. ConfiguredDirs, common]
            : ConfiguredDirs;
    }

    /// <summary>
    /// What <paramref name="backend"/> does with <see cref="ConfiguredDirs"/> at <paramref name="level"/>, said at dispatch: nothing at all
    /// (<see cref="IBackend.HonoursReadDirs"/> false), or read-write (<see cref="IBackend.ReadDirsWritableAt"/>). The git common dir alone
    /// says nothing: the api backend has no file tools to read it with, and every other one reads it.
    /// </summary>
    public (string[] Warnings, EnvironmentIssue[] Issues) ForBackend(IBackend backend, PermissionLevel level)
    {
        if (ConfiguredDirs.Length == 0)
            return ([], []);

        if (!backend.HonoursReadDirs)
        {
            string all = Where(ConfiguredDirs);
            return ([$"read_dirs: the {backend.Name} backend cannot give a job extra directories to read; {all} stay out of its reach"],
                [new EnvironmentIssue(EnvironmentIssueKind.ReadDirsUnsupported, $"the {backend.Name} backend cannot honour read_dirs", all, ConfiguredDirs.Length, Dispatch)]);
        }

        // A dir whose path the harness's rule syntax cannot fence is left out by Build (IBackend.ReadDirRefusal): said here, and an issue.
        List<string> warnings = [];
        List<string> refused = [];
        foreach (string dir in ConfiguredDirs)
        {
            if (backend.ReadDirRefusal(dir, level) is not { } refusal)
                continue;

            refused.Add(dir);
            warnings.Add($"read_dirs: {refusal}; not given to the job");
        }

        string[] given = [.. ConfiguredDirs.Except(refused, PathComparer)];
        if (given.Length > 0 && backend.ReadDirsWritableAt(level))
            warnings.Add($"read_dirs: {backend.Name} grants read_dirs read-write at level {PermissionLevelParser.ToConfigString(level)}: {Where(given)}");

        EnvironmentIssue[] issues = refused.Count == 0
            ? []
            : [new EnvironmentIssue(EnvironmentIssueKind.ReadDirsUnsupported, $"the {backend.Name} backend cannot fence these read_dirs paths", Where(refused), refused.Count, Dispatch)];
        return ([.. warnings], issues);
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

    // #146 review: a relative program (`bin/x`) would resolve against Claustrum's own cwd, which a cloned repo controls. A bare name is
    // looked up on PATH only, by WorktreeHook; an absolute path is used as is.
    private static string[]? Hook(string key, string[]? argv, string userConfigPath)
    {
        if (argv is null)
            return null;

        if (argv.Length == 0 || string.IsNullOrWhiteSpace(argv[0]))
            throw new ConfigException($"{userConfigPath}: {key} names no program; give the command as an argv array, the program first, e.g. [\"chcon\", \"-R\", \"-t\", \"container_file_t\", \"{WorktreeToken}\"]");

        string program = argv[0].Trim();
        if (program.IndexOfAny(['/', '\\']) >= 0 && !Path.IsPathFullyQualified(program))
            throw new ConfigException($"{userConfigPath}: {key}[0] '{argv[0]}' is a relative path; give an absolute path, or a bare name looked up on PATH");

        return argv;
    }

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
