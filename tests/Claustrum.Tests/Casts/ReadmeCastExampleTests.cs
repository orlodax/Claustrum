using System.Text.Json.Nodes;
using Claustrum.Casts;
using Claustrum.Roles;

namespace Claustrum.Tests.Casts;

// README.md "The cast" once showed a flat shape CastStore neither writes nor reads (#32); these
// tests keep the snippet a cast the loader accepts and the writer would produce.
public sealed class ReadmeCastExampleTests : IDisposable
{
    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-readme-cast-").FullName;
    private readonly string savedCwd = Directory.CreateTempSubdirectory("claustrum-readme-cast-saved-").FullName;

    public void Dispose()
    {
        Directory.Delete(cwd, recursive: true);
        Directory.Delete(savedCwd, recursive: true);
    }

    private static string ReadSnippet()
    {
        string[] lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "README.md"));
        int heading = Array.FindIndex(lines, line => line == "## The cast");
        Assert.True(heading >= 0, "README.md has no '## The cast' heading");

        int open = Array.FindIndex(lines, heading, line => line.TrimEnd() == "```json");
        Assert.True(open >= 0, "no ```json block after '## The cast'");

        int close = Array.FindIndex(lines, open + 1, line => line.TrimEnd() == "```");
        Assert.True(close >= 0, "the ```json block after '## The cast' is never closed");

        return string.Join('\n', lines[(open + 1)..close]);
    }

    private static string SnippetName(string snippet) =>
        JsonNode.Parse(snippet)?["name"]?.GetValue<string>() ?? throw new InvalidOperationException("README cast snippet has no 'name'");

    private Cast LoadSnippet(out string snippet)
    {
        snippet = ReadSnippet();
        string name = SnippetName(snippet);
        Directory.CreateDirectory(CastStore.DirectoryFor(cwd));
        File.WriteAllText(CastStore.PathFor(cwd, name), snippet);
        return CastStore.Load(cwd, name);
    }

    [Fact]
    public void ReadmeCastSnippetLoadsThroughCastStore()
    {
        Cast loaded = LoadSnippet(out _);

        Assert.NotEmpty(loaded.Roles);
        Assert.True(loaded.Roles.ContainsKey("builder"));
        Assert.Equal(CastArchitect.Host, loaded.Architect.Mode);
    }

    [Fact]
    public void ReadmeCastSnippetRoundTripsStructurallyIdentical()
    {
        Cast loaded = LoadSnippet(out string snippet);

        CastStore.Save(savedCwd, loaded);
        string saved = File.ReadAllText(CastStore.PathFor(savedCwd, loaded.Name));

        // Structure, not bytes: the README lays the JSON out compactly. DeepEquals also pins that
        // every null the snippet shows is a field the writer emits and nothing is dropped on load.
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(snippet), JsonNode.Parse(saved)), $"README snippet differs from what CastStore writes:\n{saved}");
    }

    [Fact]
    public void ReadmeCastSnippetNamesEveryLibraryRoleExceptTheArchitect()
    {
        Cast loaded = LoadSnippet(out _);
        string[] libraryRoles = [.. new RoleLibrary().ListRoles().Where(role => role != Cast.ArchitectRole)];

        Assert.Equal(
            libraryRoles.OrderBy(role => role, StringComparer.Ordinal),
            loaded.Roles.Keys.OrderBy(role => role, StringComparer.Ordinal));
    }
}
