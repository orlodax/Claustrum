using System.Text.Json;

namespace Claustrum.Casts;

// Reads a `cast create --answers <file>` document (docs/PLAN.md §D5). Mirrors CastStore.Load's
// JsonException wrap so a malformed answers file names itself and exits 2 (usage), not a bare
// "The JSON value could not be converted..." at exit 1 with no file name (review finding #8).
public static class CastAnswers
{
    public static Dictionary<string, string> Read(string path)
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), CastJsonContext.Default.DictionaryStringString)
                ?? throw new CastException($"'{path}' does not contain a JSON object");
        }
        catch (JsonException ex)
        {
            throw new CastException($"'{path}': {ex.Message}", ex);
        }
    }
}
