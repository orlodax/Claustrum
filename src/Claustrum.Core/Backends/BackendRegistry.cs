using System.Diagnostics.CodeAnalysis;
using Claustrum.Core.Backends.Api;
using Claustrum.Core.Backends.Claude;
using Claustrum.Core.Backends.Copilot;
using Claustrum.Core.Backends.Opencode;
using Claustrum.Core.Platform;

namespace Claustrum.Core.Backends;

// Explicit list — no assembly scanning under AOT (AGENTS.md "no reflection"). Adding a backend is
// one class + one line here + one fixtures dir (docs/PLAN.md A3).
public sealed class BackendRegistry(IEnumerable<IBackend> backends)
{
    private readonly Dictionary<string, IBackend> byName = backends.ToDictionary(backend => backend.Name);

    public IReadOnlyCollection<IBackend> All => byName.Values;

    public static BackendRegistry CreateDefault(IPlatform platform) =>
        new([new ClaudeBackend(platform), new ApiBackend(platform), new OpencodeBackend(platform), new CopilotBackend(platform)]);

    public bool TryGet(string name, [NotNullWhen(true)] out IBackend? backend) => byName.TryGetValue(name, out backend);
}
