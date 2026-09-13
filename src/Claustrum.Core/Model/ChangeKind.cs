using System.Text.Json.Serialization;
using Claustrum.Core.Json;

namespace Claustrum.Core.Model;

// Emitted as single-letter git-style codes ("A"/"M"/"D", docs/PLAN.md A2 sample), which is why
// this overrides the context-wide snake_case string-enum converter with its own.
[JsonConverter(typeof(ChangeKindJsonConverter))]
public enum ChangeKind
{
    Added,
    Modified,
    Deleted,
}
