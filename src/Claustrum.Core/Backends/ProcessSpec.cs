namespace Claustrum.Core.Backends;

// Exe is the logical command name ("claude"), not a resolved path — BinaryLocator (Process/) owns
// turning that into an actual spawnable target, including the npm-shim unwrapping on Windows.
public sealed record ProcessSpec(string Exe, string[] Args, string Cwd, Dictionary<string, string> Env, string[] TempFiles);
