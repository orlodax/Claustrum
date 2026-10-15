namespace Claustrum.Core.Config;

// #121's answer to "which alias runs which model, and why": the terminal backend and model id after
// the §A7 chase, plus the config layer that defined the first alias in the chain (null when the spec
// is not an alias at all) and whether any hop fell through to a built-in default — the silent-fallback
// case `run` and `cast create` warn about.
public sealed record ModelResolution(
    string Spec,
    string Backend,
    string ModelId,
    ConfigLayer? Layer,
    string? Source,
    bool FromBuiltin);
