using System.Text;
using Claustrum.Core.Model;
using Claustrum.Roles.Model;
using Claustrum.Roles.Templating;

namespace Claustrum.Roles;

/// <summary>
/// Renders a role for a given tier and harness (docs/PLAN.md §B2/§B3). <see cref="Render"/> is the
/// Roles → Core seam: it returns exactly the <see cref="RenderedRole"/> Core declares.
/// </summary>
public sealed class RoleRenderer(RoleLibrary library)
{
    // {{part:x}} recurses one level by design (NOTES.md "Role templating recurses one level into
    // {{part:name}}"); this is a hard backstop against a cycle in a *local* role override, where a
    // StackOverflowException would otherwise kill the process past any catch (review finding #3).
    private const int MaxPartDepth = 8;

    /// <summary>
    /// The tier's model class only — no ROLE.md rendering — so a caller can pick the harness a role
    /// will actually run on before <see cref="Render"/> needs one (review finding #2 "harness chosen
    /// before resolution").
    /// </summary>
    public string TierModelClass(string role, string tier, string cwd) =>
        RequireTier(library.LoadRole(role, cwd).Definition, role, tier).Model;

    public RenderedRole Render(string role, string tier, string harness, string cwd)
    {
        LoadedRole loaded = library.LoadRole(role, cwd);
        RoleDefinition definition = loaded.Definition;
        RoleTier roleTier = RequireTier(definition, role, tier);

        string body = TemplateRenderer.Render(loaded.RoleMdTemplate, token => ResolveToken(token, role, tier, harness, cwd, roleTier, definition));
        string houseRules = library.ReadShared("_shared/house-rules.md").Trim();
        string reportFormat = library.ReadShared($"_shared/report/{definition.Report}.md").Trim();

        string systemBody = ComposeSystemBody(body, reportFormat, houseRules);

        return new RenderedRole(
            Name: definition.Name,
            SystemBody: systemBody,
            ModelClass: roleTier.Model,
            Effort: roleTier.Effort,
            Permission: definition.Permission,
            Deny: definition.Deny,
            ReportSchema: definition.Report,
            Blind: definition.Blind);
    }

    // `## Report format` sits right after the role's opening description (before the long
    // ground-rules/how-you-work body), not at the very end after `## House rules` — a live smoke
    // test found the model skipping a report-format section that only appeared last in a long system
    // prompt on trivial one-line tasks (tester report). `body` is split on the first line-start `##`
    // heading: everything before it is the opening description, everything from it on is the rest.
    private static string ComposeSystemBody(string body, string reportFormat, string houseRules)
    {
        string trimmedBody = body.Trim();
        int headingIndex = trimmedBody.IndexOf("\n## ", StringComparison.Ordinal);
        string opening = headingIndex < 0 ? trimmedBody : trimmedBody[..headingIndex].TrimEnd();
        string rest = headingIndex < 0 ? "" : trimmedBody[(headingIndex + 1)..].TrimEnd();

        StringBuilder builder = new();
        builder.Append(opening).Append("\n\n## Report format\n\n").Append(reportFormat);
        if (rest.Length > 0)
            builder.Append('\n').Append('\n').Append(rest);
        builder.Append("\n\n## House rules\n\n").Append(houseRules);
        return builder.ToString();
    }

    /// <summary>
    /// Thin `&lt;role&gt;-xhigh`/`&lt;role&gt;-max` body, generated from `_shared/tier-stub.md` plus
    /// `role.json`'s `nonNegotiable` — never hand-written (docs/PLAN.md §B1).
    /// </summary>
    public string RenderTierStub(string role, string tier, string cwd)
    {
        LoadedRole loaded = library.LoadRole(role, cwd);
        RoleTier roleTier = RequireTier(loaded.Definition, role, tier);
        string guidance = tier switch
        {
            "xhigh" => "Use it for a subtle, cross-cutting, or high-risk case where the default depth isn't enough.",
            "max" => "Reserve it for a genuinely hard, high-stakes case, or one where a lighter pass already proved tricky or missed something.",
            _ => throw new RoleRenderException($"role '{role}': tier '{tier}' has no stub — only 'xhigh'/'max' are generated"),
        };
        string nonNegotiable = string.Join('\n', loaded.Definition.NonNegotiable.Select(line => $"- {line}"));
        string template = library.ReadShared("_shared/tier-stub.md");

        return TemplateRenderer.Render(template, token => token switch
        {
            "role" => role,
            "tier" => tier,
            "effort" => roleTier.Effort,
            "tier_guidance" => guidance,
            "non_negotiable" => nonNegotiable,
            _ => throw new RoleRenderException($"tier stub for '{role}': unknown token '{{{{{token}}}}}'"),
        }).Trim();
    }

    private static RoleTier RequireTier(RoleDefinition definition, string role, string tier) =>
        definition.Tiers.TryGetValue(tier, out RoleTier? roleTier)
            ? roleTier
            : throw new RoleRenderException($"role '{role}' has no tier '{tier}'");

    private string ResolveToken(string token, string role, string tier, string harness, string cwd, RoleTier roleTier, RoleDefinition definition, int partDepth = 0)
    {
        if (token.StartsWith("part:", StringComparison.Ordinal))
        {
            if (partDepth >= MaxPartDepth)
                throw new RoleRenderException($"role '{role}': '{{{{part:...}}}}' nesting exceeded {MaxPartDepth} levels (cycle?)");

            // Parts can themselves reference {{delegate.<role>}} (see roles/*/parts/delegation.*.md),
            // so the substitution has to recurse one more level rather than paste the raw text.
            string partText = library.ReadPart(role, token["part:".Length..], harness, cwd).Trim();
            return TemplateRenderer.Render(partText, t => ResolveToken(t, role, tier, harness, cwd, roleTier, definition, partDepth + 1));
        }

        if (token.StartsWith("delegate.", StringComparison.Ordinal))
            return RenderDelegate(token["delegate.".Length..], harness);

        return token switch
        {
            "harness" => harness,
            "role" => role,
            "tier" => tier,
            "effort" => roleTier.Effort,
            "house_rules" => library.ReadShared("_shared/house-rules.md").Trim(),
            "report_format" => library.ReadShared($"_shared/report/{definition.Report}.md").Trim(),
            _ => throw new RoleRenderException($"role '{role}': unknown token '{{{{{token}}}}}'"),
        };
    }

    // Claude has native subagents (`Agent` tool, `subagent_type`); every other harness falls back to
    // the Claustrum CLI/MCP call itself — see roles/*/parts/delegation.default.md.
    private static string RenderDelegate(string targetRole, string harness) => harness switch
    {
        "claude" => $"""the `Agent` tool with `subagent_type: "{targetRole}"` (`run_in_background: false` to block on the result)""",
        _ => $"`claustrum run {targetRole} --brief-file <path> --json`",
    };
}
