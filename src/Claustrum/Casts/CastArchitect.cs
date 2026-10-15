namespace Claustrum.Casts;

// docs/PLAN.md §D3's two modes, and the architect's own model/tier: `host` (the agent you are
// chatting with adopts the role, Model/Tier unused) or `spawned` (`claustrum coordinate` runs the
// architect role headlessly on Model). Model/Tier are optional so a cast written before M4 —
// `{"mode":"host"}` — still loads; they play the part Cast.Roles plays for every other role
// (CastApplication.Resolve), because the architect is never a member of that dictionary. Fallback/ResolvedFallback (#126) as on
// CastRoleEntry: meaningful for a spawned architect only.
public sealed record CastArchitect(string Mode, string? Model = null, string? Tier = null, string? Resolved = null, string? Fallback = null, string? ResolvedFallback = null)
{
    public const string Host = "host";
    public const string Spawned = "spawned";
}
