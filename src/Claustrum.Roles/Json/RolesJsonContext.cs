using System.Text.Json.Serialization;
using Claustrum.Roles.Model;

namespace Claustrum.Roles.Json;

/// <summary>
/// Source-generated JSON for the role library's own DTOs. Core cannot see Claustrum.Roles types, so
/// this is a second JsonSerializerContext alongside Core's ClaustrumJsonContext (architect decision,
/// see NOTES.md "Two JsonSerializerContexts").
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(RoleLibraryManifest))]
[JsonSerializable(typeof(RoleDefinition))]
[JsonSerializable(typeof(SyncManifest))]
public sealed partial class RolesJsonContext : JsonSerializerContext;
