namespace Claustrum.Core.Config;

/// <summary>Which layer of the A7 stack last set a config key — surfaced by <c>doctor</c>.</summary>
public enum ConfigLayer
{
    Default,
    User,
    Repo,
    Env,
    Flag,
}
