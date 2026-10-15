using System.Text.Json.Serialization;
using Claustrum.Core.Json;

namespace Claustrum.Core.Model;

// #126: why a `failed` run failed, as far as its backend's output says. The first four are the provider refusing the request (out of
// credits, a key it does not know, a model it cannot route, the connection) — what a cast's fallback can answer; Other is a failure the
// parser saw but could not place, or one with no provider error behind it at all. NOTES.md "A cast can name a fallback".
[JsonConverter(typeof(SnakeCaseEnumConverter<ErrorKind>))]
public enum ErrorKind
{
    ProviderQuota,
    ProviderAuth,
    ModelNotFound,
    ProviderTransport,
    Other,
}
