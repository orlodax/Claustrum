namespace Claustrum.Tests.Testing;

/// <summary>
/// The three ways a committed link carries a cast out of the working directory (#103), each built under a given cwd
/// with the real files in a separate temp directory: <c>leaf</c> (`default.json` itself), <c>claustrum</c> (the
/// `.claustrum` directory) and <c>casts</c> (the `.claustrum/casts` directory). A machine that will not make a link
/// skips the case. Dispose removes the outside tree only; the cwd belongs to the caller.
/// </summary>
internal sealed class CastLinkFixture(string cwd) : IDisposable
{
    public const string Secret = "OUTSIDE-BYTES\n";
    public const string ValidCast = /*lang=json,strict*/ """{"name":"mine","library":"1.0.0","architect":{"mode":"host"},"roles":{},"budget_usd":null}""";

    public static readonly string[] Shapes = ["leaf", "claustrum", "casts"];

    public static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private string shape = "";

    public string Outside { get; } = Directory.CreateTempSubdirectory("claustrum-cast-outside-").FullName;

    public void Dispose() => TempTree.Delete(Outside);

    /// <summary>
    /// Builds the link. Outside holds `default.json` (the secret bytes); `mine.json`, a valid cast, sits beside it,
    /// except for <c>leaf</c> where it is an ordinary file in the repo's own casts directory.
    /// </summary>
    public void Arrange(string linkShape)
    {
        shape = linkShape;
        string claustrum = Path.Combine(cwd, ".claustrum");
        switch (linkShape)
        {
            case "leaf":
                Directory.CreateDirectory(Path.Combine(claustrum, "casts"));
                File.WriteAllText(Path.Combine(claustrum, "casts", "mine.json"), ValidCast);
                File.WriteAllText(RealPathFor("default"), Secret);
                TestLinks.Create(Path.Combine(claustrum, "casts", "default.json"), RealPathFor("default"));
                break;
            case "claustrum":
                WriteOutside();
                TestLinks.CreateDirectoryLink(claustrum, Path.Combine(Outside, "claustrum"));
                break;
            case "casts":
                WriteOutside();
                Directory.CreateDirectory(claustrum);
                TestLinks.CreateDirectoryLink(Path.Combine(claustrum, "casts"), Path.Combine(Outside, "casts"));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(linkShape), linkShape, "unknown link shape");
        }
    }

    /// <summary>Where the cast file really lives once the link is followed.</summary>
    public string RealPathFor(string name) => shape switch
    {
        "leaf" => Path.Combine(Outside, "target.json"),
        "claustrum" => Path.Combine(Outside, "claustrum", "casts", $"{name}.json"),
        _ => Path.Combine(Outside, "casts", $"{name}.json"),
    };

    /// <summary>Every entry under the outside tree with its bytes: equal before and after means nothing was written or created there.</summary>
    public string[] Snapshot() =>
        [.. Directory.GetFileSystemEntries(Outside, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(entry => File.Exists(entry) ? $"{entry}={File.ReadAllText(entry)}" : entry)];

    private void WriteOutside()
    {
        string casts = shape == "claustrum" ? Path.Combine(Outside, "claustrum", "casts") : Path.Combine(Outside, "casts");
        Directory.CreateDirectory(casts);
        File.WriteAllText(Path.Combine(casts, "default.json"), Secret);
        File.WriteAllText(Path.Combine(casts, "mine.json"), ValidCast);
    }
}
