using System.Text.RegularExpressions;

namespace Claustrum.Roles.Templating;

/// <summary>Plain `{{token}}` substitution (docs/PLAN.md §B2) — no templating engine, no conditionals/loops.</summary>
public static partial class TemplateRenderer
{
    [GeneratedRegex(@"\{\{([^{}]+)\}\}")]
    private static partial Regex TokenPattern();

    /// <summary><paramref name="resolve"/> should throw for a token it does not recognize — that becomes the render error.</summary>
    public static string Render(string template, Func<string, string> resolve) =>
        TokenPattern().Replace(template, match => resolve(match.Groups[1].Value));
}
