using System.Text.Json.Serialization;

namespace Claustrum.Casts;

// JSON shape for `claustrum init questions --json`. Defined here so serialization works
// through the source generator. Also handles the --answers file type for convenience.
[JsonSerializable(typeof(InitQuestionsResult))]
[JsonSerializable(typeof(Dictionary<string, string>))]
public sealed partial class InitQuestionsJsonContext : JsonSerializerContext;

// #122: the init questionnaire: one question per built-in alias the user config does not define,
// plus the user config path the answers would be written to.
public sealed record InitQuestionsResult(CastQuestion[] Questions, string UserConfigPath);
