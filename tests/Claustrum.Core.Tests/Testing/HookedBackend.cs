using Claustrum.Core.Backends;
using Claustrum.Core.Config;

namespace Claustrum.Core.Tests.Testing;

// A ScriptedBackend that also implements IPostRunHook (#125), recording what Runner handed each call. The hook's answers are
// delegates, so a test says in one line what the "export" returns, throws or never gets asked.
public sealed class HookedBackend(
    ScriptedBackend inner,
    Func<PostRunContext, PreRunBaseline?>? preRun = null,
    Func<ParsedOutput, PostRunContext, PreRunBaseline?, PostRunResult?>? postRun = null,
    Func<ParsedOutput, PostRunContext, PreRunBaseline?, CancellationToken, Task<PostRunResult?>>? asyncPostRun = null) : IBackend, IPostRunHook
{
    public string Name => inner.Name;

    public int PreRunCalls { get; private set; }

    public int PostRunCalls { get; private set; }

    public PostRunContext? PreRunContext { get; private set; }

    public PostRunContext? PostRunContext { get; private set; }

    public PreRunBaseline? PostRunBaseline { get; private set; }

    public Task<Doctor> DetectAsync(BackendConfig? config, CancellationToken cancellationToken) => inner.DetectAsync(config, cancellationToken);

    public ProcessSpec Build(ResolvedRun run) => inner.Build(run);

    public ParsedOutput Parse(string stdout, string stderr, int exitCode) => inner.Parse(stdout, stderr, exitCode);

    public Task<PreRunBaseline?> PreRunAsync(PostRunContext context, CancellationToken cancellationToken)
    {
        PreRunCalls++;
        PreRunContext = context;
        return Task.FromResult(preRun?.Invoke(context));
    }

    public Task<PostRunResult?> PostRunAsync(ParsedOutput parsed, PostRunContext context, PreRunBaseline? baseline, CancellationToken cancellationToken)
    {
        PostRunCalls++;
        PostRunContext = context;
        PostRunBaseline = baseline;
        return asyncPostRun is not null ? asyncPostRun(parsed, context, baseline, cancellationToken) : Task.FromResult(postRun?.Invoke(parsed, context, baseline));
    }
}
