namespace Claustrum.Core.Process;

public sealed class BackendNotFoundException(string backendName) : Exception($"backend '{backendName}' was not found on PATH");
