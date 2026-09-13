namespace Claustrum.Core.Backends;

public sealed record Doctor(bool Found, string? Path, string? Version, string[] Problems);
