using Claustrum.Core.Config;

namespace Claustrum.Delegation;

// What `coordinate --resume` needs to rebuild a coordinate job's plan (#124 stage 3b), carried in the architect's DelegateRequest so
// resume.json holds it: the cast's file name and the architect flags as they were typed (not as the cast resolved them). TreeId is the
// budget tree a resumed architect continues, null for a job that started its own.
public sealed record CoordinateOrigin(string CastName, string? TierFlag, ConfigOverrides Overrides, string? TreeId = null);
