namespace Claustrum.Core.Config;

// Mirrors the whole claustrum.json shape (docs/PLAN.md A7) so every layer — built-in, user, repo,
// env — deserializes into the same type before Config merges them in order.
public sealed record ConfigDocument(
    Dictionary<string, string>? Models,
    Dictionary<string, RoleSettings>? Roles,
    Dictionary<string, BackendConfig>? Backends,
    DefaultsSettings? Defaults,
    JobsSettings? Jobs);
