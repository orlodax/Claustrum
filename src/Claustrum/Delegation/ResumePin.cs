namespace Claustrum.Delegation;

// #124 stage 3 review F5: the backend, model and permission level a `needs_input` job ran as, read from its receipt and backend.json. A
// continuation resumes that job's session, which only its own backend and model can read, so DelegateEngine.Prepare resolves to these
// and not to what the configuration says today. Permission is null for a job that predates backend.json recording it.
public sealed record ResumePin(string Backend, string Model, string? Permission);
