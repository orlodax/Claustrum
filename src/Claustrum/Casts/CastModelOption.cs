namespace Claustrum.Casts;

// One row of the `models` table that CastQuestionnaireResult carries (#121): what each alias in the
// merged claustrum.json resolves to, which config layer defined it, and where it came from (the
// human-readable source string). Cast questions shows this before asking anything so the human can
// see the alias → model mapping (and a silent built-in fallback) up front.
public sealed record CastModelOption(string Alias, string Target, string Layer, string Source);
