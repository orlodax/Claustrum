namespace Claustrum.Roles.Tests;

internal static class Prose
{
    /// <summary>
    /// Every run of whitespace, newlines included, collapsed to one space: the role prose is wrapped
    /// at ~100 columns in the source, so a phrase an assertion looks for may sit across a line break
    /// today and on one line after the next re-wrap.
    /// </summary>
    public static string Flatten(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
