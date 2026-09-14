namespace Claustrum.Roles.Sync;

/// <summary>
/// `claustrum sync`'s three modes (docs/PLAN.md §B4/§D5): <see cref="Write"/> is the M1 default;
/// <see cref="DryRun"/> and <see cref="Check"/> classify files exactly the same way but never touch
/// disk — <see cref="Check"/> is for CI (nonzero exit on anything not already up to date),
/// <see cref="DryRun"/> additionally hands back each proposed file's content for a diff.
/// </summary>
public enum SyncMode
{
    Write,
    DryRun,
    Check,
}
