namespace Claustrum.Tests.Testing;

/// <summary>One way a `coordinate` repository can differ from HEAD, and what the refusal must say (#74).</summary>
/// <param name="Fragment">A piece of the message the operator sees.</param>
/// <param name="CastName">The cast the case asks for, or null for the default one.</param>
/// <param name="Cwd">Where `coordinate` is started from.</param>
internal sealed record PreconditionCase(string Fragment, string? CastName, string Cwd);

/// <summary>
/// The refusals both front doors of `coordinate` share, applied to a <see cref="IsolatedRepo.ForCoordinate"/>
/// repository. ArchitectWorktreeTests has the full matrix at the precondition's own level; the door tests run
/// these through the CLI binary and the MCP tool, which is where exit 2, the tool error and "no job directory"
/// are observable.
/// </summary>
internal static class PreconditionCases
{
    public static TheoryData<string> Names =>
    [
        "modified-claustrum-json",
        "deleted-claustrum-json",
        "staged-claustrum-json",
        "modified-cast",
        "untracked-cast",
        "ignored-cast",
        "staged-new-cast",
        "untracked-role-file",
        "whitelist-gitignore",
        "uncommitted-gitignore",
        "skip-worktree-claustrum-json",
        "assume-unchanged-claustrum-json",
        "untracked-mcp-json",
        "show-untracked-files-no",
        "subdirectory",
    ];

    public static PreconditionCase Apply(IsolatedRepo repo, string name)
    {
        string root = repo.Repo;

        switch (name)
        {
            case "modified-claustrum-json":
                File.AppendAllText(Path.Combine(root, "claustrum.json"), "\n");
                return new PreconditionCase(Refused("claustrum.json", "uncommitted changes"), null, root);
            case "deleted-claustrum-json":
                File.Delete(Path.Combine(root, "claustrum.json"));
                return new PreconditionCase(Refused("claustrum.json", "uncommitted changes"), null, root);
            case "staged-claustrum-json":
                File.AppendAllText(Path.Combine(root, "claustrum.json"), "\n");
                repo.Git("add", "claustrum.json");
                return new PreconditionCase(Refused("claustrum.json", "uncommitted changes"), null, root);
            case "modified-cast":
                File.AppendAllText(CastPath(root, "default"), "\n");
                return new PreconditionCase(TrackedCastRefused("default"), null, root);
            case "untracked-cast":
                File.Copy(CastPath(root, "default"), CastPath(root, "other"));
                return new PreconditionCase(CastWithoutRule("other", "untracked"), "other", root);
            case "ignored-cast":
                File.WriteAllText(Path.Combine(root, ".gitignore"), IsolatedRepo.MachineryIgnoreRules + ".claustrum/casts/other.json\n");
                repo.Git("add", ".gitignore");
                repo.Git("commit", "-q", "-m", "rules");
                File.Copy(CastPath(root, "default"), CastPath(root, "other"));
                return new PreconditionCase(CastWithoutRule("other", "ignored"), "other", root);
            case "staged-new-cast":
                File.Copy(CastPath(root, "default"), CastPath(root, "other"));
                repo.Git("add", ".claustrum/casts/other.json");
                return new PreconditionCase(TrackedCastRefused("other"), "other", root);
            case "untracked-role-file":
                Directory.CreateDirectory(Path.Combine(root, ".claustrum", "roles", "tester"));
                File.WriteAllText(Path.Combine(root, ".claustrum", "roles", "tester", "ROLE.md"), "x\n");
                return new PreconditionCase(Refused(".claustrum/roles/tester/ROLE.md", "untracked"), null, root);
            case "whitelist-gitignore":
                File.WriteAllText(Path.Combine(root, ".gitignore"), "*\n!*/\n!*.json\n!*.md\n!*.txt\n!.gitignore\n");
                repo.Git("add", ".gitignore");
                repo.Git("commit", "-q", "-m", "whitelist");
                return new PreconditionCase("git-ignore .claustrum/worktrees/, .claustrum/briefs/ in a committed .gitignore first", null, root);
            case "uncommitted-gitignore":
                File.WriteAllText(Path.Combine(root, ".gitignore"), "");
                repo.Git("add", ".gitignore");
                repo.Git("commit", "-q", "-m", "no rules");
                File.WriteAllText(Path.Combine(root, ".gitignore"), IsolatedRepo.MachineryIgnoreRules);
                return new PreconditionCase(".gitignore (uncommitted changes — the worktree gets HEAD's ignore rules; commit it first)", null, root);
            case "skip-worktree-claustrum-json":
                repo.Git("update-index", "--skip-worktree", "claustrum.json");
                File.AppendAllText(Path.Combine(root, "claustrum.json"), "\n");
                return new PreconditionCase(Refused("claustrum.json", "skip-worktree"), null, root);
            case "assume-unchanged-claustrum-json":
                repo.Git("update-index", "--assume-unchanged", "claustrum.json");
                File.AppendAllText(Path.Combine(root, "claustrum.json"), "\n");
                return new PreconditionCase(Refused("claustrum.json", "assume-unchanged"), null, root);
            case "untracked-mcp-json":
                File.WriteAllText(Path.Combine(root, ".mcp.json"), "{}\n");
                return new PreconditionCase("commit or git-ignore .mcp.json (untracked) — an ignored local copy is fine", null, root);
            case "show-untracked-files-no":
                repo.Git("config", "status.showUntrackedFiles", "no");
                File.Copy(CastPath(root, "default"), CastPath(root, "other"));
                return new PreconditionCase(CastWithoutRule("other", "untracked"), "other", root);
            case "subdirectory":
                // With a cast of its own, or the cast lookup would answer first: the case the refusal exists for.
                string sub = Path.Combine(root, "sub");
                Directory.CreateDirectory(Path.Combine(sub, ".claustrum", "casts"));
                File.Copy(CastPath(root, "default"), Path.Combine(sub, ".claustrum", "casts", "default.json"));
                return new PreconditionCase($"run it from {root} (or --cwd \"{root}\")", null, sub);
            default:
                throw new ArgumentOutOfRangeException(nameof(name), name, "unknown precondition case");
        }
    }

    // #122: a cast is judged by its own git state. Untracked or ignored, it needs a committed rule ignoring `.claustrum/casts/`
    // (what `claustrum init` writes), or the architect's `git add -A` would commit the copy it is handed; the message names init.
    private static string CastWithoutRule(string castName, string state) =>
        $"the cast '{castName}' is {state}, but no committed .gitignore rule ignores .claustrum/casts/; run `claustrum init` and commit .gitignore first";

    // Tracked with a local change (or staged new): the worktree would get HEAD's copy, so it is committed first. The path is the
    // cast's own, spelled by the OS, so only its tail is matched.
    private static string TrackedCastRefused(string castName) => $"{castName}.json (uncommitted changes — commit it first)";

    private static string Refused(string file, string state) => $"commit (or un-ignore) {file} ({state}) first";

    private static string CastPath(string root, string castName) => Path.Combine(root, ".claustrum", "casts", $"{castName}.json");
}
