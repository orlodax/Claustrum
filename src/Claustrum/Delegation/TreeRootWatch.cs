using Claustrum.Core.Jobs;
using Claustrum.Live;

namespace Claustrum.Delegation;

// #124 stage 2 review F1 (NOTES.md "Detached hosts and tree routing"): a detached host is not a child of the process that started it,
// so a `coordinate` that ends, is killed or is cancelled cannot take its tree's hosts down with `Kill(entireProcessTree)`. Each host
// therefore asks, every few seconds, whether the coordinate job it hangs from is still alive: its result.json absent and its owner
// (owner.json: pid + start time, `coordinate` writes it first thing) running. The tree id is taken as it is everywhere else: a name
// that is no job, or a job that never recorded an owner (a tree id exported by hand), is not a root, and nothing is cancelled for it.
internal sealed class TreeRootWatch
{
    private const int MaxSuccessorHops = 16;

    private readonly LiveJob root;
    private readonly OwnerRecord owner;

    private TreeRootWatch(LiveJob root, OwnerRecord owner)
    {
        this.root = root;
        this.owner = owner;
    }

    /// <summary>The watch for the tree this process runs in, or null outside a tree or when the tree's root is not a coordinate job with an owner.</summary>
    public static TreeRootWatch? ForEnvironment()
    {
        try
        {
            return BudgetLedger.TreeIdFor(AppServices.Platform) is { } tree && LiveRoot(tree) is { } root && root.ReadOwner() is { } owner
                ? new TreeRootWatch(root, owner)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A store that cannot be read costs the watch, never the job it was to guard.
            return null;
        }
    }

    // A coordinate job continued by `coordinate --resume` (#124 stage 3b) names the job that took over its tree; the chain is followed to
    // its end, so a host started under the tree watches the architect that is running, not the one whose receipt was written long ago.
    private static LiveJob? LiveRoot(string tree)
    {
        LiveJob? root = LiveJob.Find(tree);
        for (int hop = 0; hop < MaxSuccessorHops && root?.ReadSuccessor() is { } next && LiveJob.Find(next) is { } successor; hop++)
            root = successor;

        return root;
    }

    /// <summary>
    /// Whether the root has ended: it wrote its receipt, or its owner is gone. The owner is read once, so a root directory pruned
    /// from the store meanwhile is judged by its process alone, never taken for ended on a guess.
    /// </summary>
    public bool HasEnded() => root.HasResult || !owner.IsAlive();
}
