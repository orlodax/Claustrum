using System.Globalization;
using System.Text.Json;
using Claustrum.Core.Git;
using Claustrum.Core.Json;
using Claustrum.Core.Model;
using Claustrum.Core.Platform;

namespace Claustrum.Core.Config;

// Layered load per docs/PLAN.md A7: built-in defaults -> user file -> repo file -> CLAUSTRUM_* env
// -> caller-supplied ConfigOverrides, later wins per key, deny lists concatenate. Origins records
// which layer last touched each key, for the future `doctor` command.
public sealed class Config
{
    public required ConfigDocument Merged { get; init; }

    // Dictionary, not IReadOnlyDictionary: Resolve records ConfigLayer.Flag entries for the same
    // instance after Load hands it back, so `backends doctor` (and any future caller) sees flag
    // overrides too, not just the four file/env layers (review finding #2).
    public required Dictionary<string, ConfigLayer> Origins { get; init; }

    public static Config Load(IPlatform platform, string cwd)
    {
        Dictionary<string, ConfigLayer> origins = [];
        ConfigDocument merged = TrackDefaults(BuiltInDefaults(), ConfigLayer.Builtin, origins);

        string userPath = UserConfigPath(platform);
        if (platform.FileExists(userPath))
            merged = MergeLayer(merged, ReadDocument(platform, userPath), ConfigLayer.User, origins);

        string? gitRoot = GitRootLocator.Find(cwd, platform);
        if (gitRoot is not null)
        {
            string repoPath = Path.Combine(gitRoot, "claustrum.json");
            if (platform.FileExists(repoPath))
                merged = MergeLayer(merged, ReadDocument(platform, repoPath), ConfigLayer.Repo, origins);
        }

        if (ReadEnvLayer(platform) is { } envLayer)
            merged = MergeLayer(merged, envLayer, ConfigLayer.Env, origins);

        return new Config { Merged = merged, Origins = origins };
    }

    // Model class -> alias -> "[backend:]model-id" (docs/PLAN.md A7); role.json's Permission
    // string form composes with the config-layer and flag string forms via PermissionLevelParser.
    public ResolvedRole Resolve(RenderedRole role, ConfigOverrides overrides)
    {
        RoleSettings? roleSettings = Merged.Roles is not null && Merged.Roles.TryGetValue(role.Name, out RoleSettings? found)
            ? found
            : null;

        string modelSpec = overrides.Model ?? roleSettings?.Model ?? role.ModelClass;
        (string backendFromModel, string modelId) = ResolveModel(modelSpec);
        string backend = overrides.Backend ?? backendFromModel;

        string effort = overrides.Effort ?? roleSettings?.Effort ?? role.Effort;

        string permissionString = overrides.Permission ?? roleSettings?.Permission ?? role.Permission;
        if (!PermissionLevelParser.TryParse(permissionString, out PermissionLevel level))
            throw new ConfigException($"unknown permission '{permissionString}'");

        string[] deny = [.. role.Deny, .. roleSettings?.Deny ?? [], .. overrides.Deny ?? []];

        RecordFlagOrigins(role.Name, overrides);

        return new ResolvedRole(role.Name, role.SystemBody, backend, modelId, effort, new PermissionPolicy(level, deny), role.Blind, role.ReportSchema is { Length: > 0 });
    }

    // Exposes the alias chase for callers that only have a bare model spec, not a role — the cast
    // questionnaire (docs/PLAN.md §D2) needs to know which of claustrum.json's own model aliases
    // resolve to a backend that is actually installed, before it can offer them as live options.
    public string ResolveModelBackend(string modelSpec) => ResolveModel(modelSpec).Backend;

    // Mirrors Resolve's model-spec precedence (flag > per-role config > the role's own tier class)
    // without duplicating the alias chase, so the CLI can pick the harness a role will run on
    // *before* it has anything to Render (review finding #2 "harness chosen before resolution") —
    // Render needs a harness up front, but the backend that harness implies needs this config.
    public string ResolveBackend(string roleName, string tierModelClass, ConfigOverrides overrides)
    {
        RoleSettings? roleSettings = Merged.Roles is not null && Merged.Roles.TryGetValue(roleName, out RoleSettings? found)
            ? found
            : null;

        string modelSpec = overrides.Model ?? roleSettings?.Model ?? tierModelClass;
        (string backendFromModel, _) = ResolveModel(modelSpec);
        return overrides.Backend ?? backendFromModel;
    }

    private void RecordFlagOrigins(string roleName, ConfigOverrides overrides)
    {
        if (overrides.Backend is not null)
            Origins[$"roles.{roleName}.backend"] = ConfigLayer.Flag;
        if (overrides.Model is not null)
            Origins[$"roles.{roleName}.model"] = ConfigLayer.Flag;
        if (overrides.Effort is not null)
            Origins[$"roles.{roleName}.effort"] = ConfigLayer.Flag;
        if (overrides.Permission is not null)
            Origins[$"roles.{roleName}.permission"] = ConfigLayer.Flag;
        if (overrides.Deny is { Length: > 0 })
            Origins[$"roles.{roleName}.deny"] = ConfigLayer.Flag;
        if (overrides.BudgetUsd is not null)
            Origins["defaults.budget_usd"] = ConfigLayer.Flag;
        if (overrides.TimeoutSeconds is not null)
            Origins["defaults.timeout_seconds"] = ConfigLayer.Flag;
    }

    // §A7 aliases resolve recursively to depth 3: {"a":"b","b":"c","c":"claude:opus"} must resolve
    // ("a" is 3 hops from its terminal value). The loop below chases up to 3 hops and only then
    // checks whether the landing spot is itself still an alias — checking termination inside the
    // same bounded loop (as a first build did) needs a 4th iteration to notice hop 3 was terminal,
    // silently capping real resolution at 2 hops.
    private (string Backend, string ModelId) ResolveModel(string spec)
    {
        HashSet<string> seen = [spec];
        string current = spec;
        for (int hop = 0; hop < 3; hop++)
        {
            if (Merged.Models is null || !Merged.Models.TryGetValue(current, out string? next))
                return SplitBackendModel(current);

            if (!seen.Add(next))
                throw new ConfigException($"model alias '{spec}' has a cycle at '{next}'");

            current = next;
        }

        if (Merged.Models is not null && Merged.Models.ContainsKey(current))
            throw new ConfigException($"model alias '{spec}' did not resolve within 3 levels");

        return SplitBackendModel(current);
    }

    private static (string Backend, string ModelId) SplitBackendModel(string value)
    {
        int colon = value.IndexOf(':');
        return colon < 0 ? ("claude", value) : (value[..colon], value[(colon + 1)..]);
    }

    // One alias per class in roles/library.json, so `claustrum run <role>` resolves a real model
    // without `--model` and without a `claustrum.json` (builder brief item 2); claustrum.json's
    // `models` layer still overrides any of these per key (MergeLayer runs after this).
    private static ConfigDocument BuiltInDefaults() => new(
        Models: new Dictionary<string, string>
        {
            ["frontier-reasoning"] = "claude:opus",
            ["frontier-coding"] = "claude:opus",
            ["standard-coding"] = "claude:sonnet",
            ["cheap-coding"] = "claude:haiku",
            ["fast"] = "claude:haiku",
        },
        Roles: [],
        Backends: [],
        Defaults: new DefaultsSettings(TimeoutSeconds: 1800, BudgetUsd: 5m, EnvPassthrough: "allowlist"),
        Jobs: new JobsSettings(KeepLast: 200));

    private static ConfigDocument TrackDefaults(ConfigDocument doc, ConfigLayer layer, Dictionary<string, ConfigLayer> origins)
    {
        foreach ((string modelClass, _) in doc.Models ?? [])
            origins[$"models.{modelClass}"] = layer;
        if (doc.Defaults?.TimeoutSeconds is not null)
            origins["defaults.timeout_seconds"] = layer;
        if (doc.Defaults?.BudgetUsd is not null)
            origins["defaults.budget_usd"] = layer;
        if (doc.Defaults?.EnvPassthrough is not null)
            origins["defaults.env_passthrough"] = layer;
        if (doc.Jobs?.KeepLast is not null)
            origins["jobs.keep_last"] = layer;

        return doc;
    }

    private static ConfigDocument MergeLayer(ConfigDocument baseDoc, ConfigDocument overlay, ConfigLayer layer, Dictionary<string, ConfigLayer> origins)
    {
        Dictionary<string, string> models = new(baseDoc.Models ?? []);
        foreach (KeyValuePair<string, string> entry in overlay.Models ?? [])
        {
            models[entry.Key] = entry.Value;
            origins[$"models.{entry.Key}"] = layer;
        }

        Dictionary<string, RoleSettings> roles = new(baseDoc.Roles ?? []);
        foreach (KeyValuePair<string, RoleSettings> entry in overlay.Roles ?? [])
            roles[entry.Key] = MergeRole(roles.GetValueOrDefault(entry.Key), entry.Value, entry.Key, layer, origins);

        Dictionary<string, BackendConfig> backends = new(baseDoc.Backends ?? []);
        foreach (KeyValuePair<string, BackendConfig> entry in overlay.Backends ?? [])
            backends[entry.Key] = MergeBackend(backends.GetValueOrDefault(entry.Key), entry.Value, entry.Key, layer, origins);

        return new ConfigDocument(models, roles, backends, MergeDefaults(baseDoc.Defaults, overlay.Defaults, layer, origins), MergeJobs(baseDoc.Jobs, overlay.Jobs, layer, origins));
    }

    private static RoleSettings MergeRole(RoleSettings? existing, RoleSettings incoming, string name, ConfigLayer layer, Dictionary<string, ConfigLayer> origins)
    {
        if (incoming.Model is not null)
            origins[$"roles.{name}.model"] = layer;
        if (incoming.Effort is not null)
            origins[$"roles.{name}.effort"] = layer;
        if (incoming.Permission is not null)
            origins[$"roles.{name}.permission"] = layer;

        string[]? deny = existing?.Deny;
        if (incoming.Deny is { Length: > 0 })
        {
            deny = [.. existing?.Deny ?? [], .. incoming.Deny];
            origins[$"roles.{name}.deny"] = layer;
        }

        return new RoleSettings(incoming.Model ?? existing?.Model, incoming.Effort ?? existing?.Effort, incoming.Permission ?? existing?.Permission, deny);
    }

    private static BackendConfig MergeBackend(BackendConfig? existing, BackendConfig incoming, string name, ConfigLayer layer, Dictionary<string, ConfigLayer> origins)
    {
        if (incoming.Path is not null)
            origins[$"backends.{name}.path"] = layer;
        if (incoming.Injection is not null)
            origins[$"backends.{name}.injection"] = layer;

        return new BackendConfig(incoming.Path ?? existing?.Path, incoming.Injection ?? existing?.Injection);
    }

    private static DefaultsSettings? MergeDefaults(DefaultsSettings? existing, DefaultsSettings? incoming, ConfigLayer layer, Dictionary<string, ConfigLayer> origins)
    {
        if (incoming is null)
            return existing;

        if (incoming.TimeoutSeconds is not null)
            origins["defaults.timeout_seconds"] = layer;
        if (incoming.BudgetUsd is not null)
            origins["defaults.budget_usd"] = layer;
        if (incoming.EnvPassthrough is not null)
            origins["defaults.env_passthrough"] = layer;

        return new DefaultsSettings(incoming.TimeoutSeconds ?? existing?.TimeoutSeconds, incoming.BudgetUsd ?? existing?.BudgetUsd, incoming.EnvPassthrough ?? existing?.EnvPassthrough);
    }

    private static JobsSettings? MergeJobs(JobsSettings? existing, JobsSettings? incoming, ConfigLayer layer, Dictionary<string, ConfigLayer> origins)
    {
        if (incoming?.KeepLast is null)
            return existing ?? incoming;

        origins["jobs.keep_last"] = layer;
        return new JobsSettings(incoming.KeepLast);
    }

    private static string UserConfigPath(IPlatform platform) => platform.Os == ClaustrumOs.Windows
        ? Path.Combine(platform.GetEnvironmentVariable("APPDATA") ?? Path.Combine(platform.HomeDirectory, "AppData", "Roaming"), "claustrum", "config.json")
        : Path.Combine(platform.HomeDirectory, ".config", "claustrum", "config.json");

    private static ConfigDocument ReadDocument(IPlatform platform, string path)
    {
        string json = platform.ReadAllText(path);
        try
        {
            ConfigDocument? document = JsonSerializer.Deserialize(json, ClaustrumJsonContext.Default.ConfigDocument);
            return document ?? throw new ConfigException($"'{path}' does not contain a JSON object");
        }
        catch (JsonException ex)
        {
            // ex.Message already carries the line/byte position; naming the file here is what a bare
            // JsonException wouldn't do (review finding #1).
            throw new ConfigException($"'{path}': {ex.Message}", ex);
        }
    }

    private static ConfigDocument? ReadEnvLayer(IPlatform platform)
    {
        int? timeoutSeconds = ParseInt(platform.GetEnvironmentVariable("CLAUSTRUM_TIMEOUT_SECONDS"));
        decimal? budgetUsd = ParseDecimal(platform.GetEnvironmentVariable("CLAUSTRUM_BUDGET_USD"));
        string? envPassthrough = platform.GetEnvironmentVariable("CLAUSTRUM_ENV_PASSTHROUGH");

        return timeoutSeconds is null && budgetUsd is null && envPassthrough is null
            ? null
            : new ConfigDocument(null, null, null, new DefaultsSettings(timeoutSeconds, budgetUsd, envPassthrough), null);
    }

    private static int? ParseInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) ? result : null;

    private static decimal? ParseDecimal(string? value) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal result) ? result : null;
}
