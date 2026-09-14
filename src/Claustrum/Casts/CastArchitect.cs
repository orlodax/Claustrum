namespace Claustrum.Casts;

// docs/PLAN.md §D3: "host" (the chatting agent adopts the architect role itself) is the only mode
// any code path acts on in M2 — "spawned" is recorded so a cast written now still round-trips once
// `coordinate` (§D3, issue #5/M4) exists, but nothing reads Mode today.
public sealed record CastArchitect(string Mode);
