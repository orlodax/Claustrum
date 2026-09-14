using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Claustrum.Casts;
using Claustrum.Core.Json;
using Claustrum.Mcp;
using Claustrum.Mcp.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Claustrum.Cli;

// docs/PLAN.md §A6 `claustrum mcp [--cwd <dir>]`. Config root is the launch cwd (hosts launch MCP
// servers in the workspace); `--cwd` overrides it up front, per-call `cwd` is a delegate/M3 concern.
// Logging goes to stderr — stdout carries only the MCP JSON-RPC protocol.
public static class McpCommand
{
    public static Command Build()
    {
        Option<string?> cwd = new("--cwd") { Description = "Working directory for the server (default: current directory)." };
        Command command = new("mcp", "Run the Claustrum MCP server over stdio.") { cwd };
        command.SetAction(async parseResult => await RunAsync(parseResult.GetValue(cwd)));

        return command;
    }

    private static async Task RunAsync(string? cwdOption)
    {
        if (cwdOption is { Length: > 0 })
            Environment.CurrentDirectory = Path.GetFullPath(cwdOption);

        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

        // One combined resolver over every source-generated context a tool's parameters/return type
        // can reach (docs/PLAN.md §A1 "exact 2.2.0 overload UNCONFIRMED — verify at M2 by
        // AOT-publishing with zero IL2026/IL3050 warnings" — confirmed: WithTools<T>(JsonSerializerOptions)
        // is the real overload, and this is what it needs to build tool schemas without reflection).
        JsonSerializerOptions jsonOptions = new()
        {
            TypeInfoResolver = JsonTypeInfoResolver.Combine(ClaustrumJsonContext.Default, CastJsonContext.Default, McpJsonContext.Default),
        };

        builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<ClaustrumTools>(jsonOptions);

        await builder.Build().RunAsync();
    }
}
