using Claustrum.Core.Config;
using CoreConfig = Claustrum.Core.Config.Config;

namespace Claustrum.Roles.Tests.Sync;

// A merged claustrum.json built in memory: the built-in model classes (what Config.Load starts from) plus whatever a test
// layers on top, so Choose and the syncs see the same aliases a real run would.
internal static class SyncConfigs
{
    public static CoreConfig Make(Dictionary<string, string>? models = null, Dictionary<string, RoleSettings>? roles = null)
    {
        Dictionary<string, string> merged = new(StringComparer.Ordinal)
        {
            ["frontier-reasoning"] = "claude:opus",
            ["frontier-coding"] = "claude:opus",
            ["standard-coding"] = "claude:sonnet",
            ["cheap-coding"] = "claude:haiku",
            ["fast"] = "claude:haiku",
        };
        foreach ((string alias, string target) in models ?? [])
            merged[alias] = target;

        return new CoreConfig { Merged = new ConfigDocument(merged, roles ?? [], Backends: [], Defaults: null, Jobs: null), Origins = [] };
    }
}
