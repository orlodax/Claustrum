using Claustrum.Core.Backends;
using Claustrum.Core.Config;

namespace Claustrum.Tests.Testing;

// A minimal IBackend for CastQuestionnaireTests: only DetectAsync is exercised (the questionnaire
// never Builds/Parses), so Build/Parse throw the way Claustrum.Core.Tests' ScriptedBackend does for
// members its own tests don't reach.
public sealed class FakeBackend(string name, bool found) : IBackend
{
    public string Name => name;

    public Task<Doctor> DetectAsync(BackendConfig? config, CancellationToken cancellationToken) =>
        Task.FromResult(found ? new Doctor(true, "/usr/bin/fake", "1.0.0", []) : new Doctor(false, null, null, [$"'{name}' was not found on PATH"]));

    public ProcessSpec Build(ResolvedRun run) => throw new NotSupportedException("not exercised by CastQuestionnaireTests");

    public ParsedOutput Parse(string stdout, string stderr, int exitCode) => throw new NotSupportedException("not exercised by CastQuestionnaireTests");
}
