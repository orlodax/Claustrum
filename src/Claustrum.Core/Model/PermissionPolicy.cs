namespace Claustrum.Core.Model;

// Deny is a harness-neutral pattern list ("git push", "rm -rf"); a backend without a native deny
// mechanism folds it into the system prompt instead (NOTES.md "Role injection per backend").
public sealed record PermissionPolicy(PermissionLevel Level, string[] Deny);
