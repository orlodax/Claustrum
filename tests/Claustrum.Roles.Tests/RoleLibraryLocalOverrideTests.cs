using System.Runtime.Versioning;
using Claustrum.Roles.Tests.Sync;

namespace Claustrum.Roles.Tests;

// docs/PLAN.md §B4 "Local override ... role.json deep-merged"; RoleLibrary.DeepMerge recurses into
// nested JSON objects but replaces arrays wholesale (a JsonArray is never a JsonObject).
// #97: an override file is read only when its real path (the leaf link included) stays inside the cwd; a null cwd (#99) reads
// the library alone; HasLocalOverride is existence-only, so it neither reads nor throws.
public sealed class RoleLibraryLocalOverrideTests : IDisposable
{
    private const string Secret = "TOP-SECRET-TOKEN";

    private static readonly StringComparison pathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-role-override-").FullName;
    private readonly string outside = Directory.CreateTempSubdirectory("claustrum-role-outside-").FullName;

    public void Dispose()
    {
        LinkFixtures.DeleteTree(cwd);
        LinkFixtures.DeleteTree(outside);
    }

    private string RoleDirectory => Path.Combine(cwd, ".claustrum", "roles", "builder");

    private string OverrideFile(params string[] relative) => Path.Combine([RoleDirectory, .. relative]);

    // A file under `outside` holding the secret, and a link at `linkPath` (created with its directory) pointing to it.
    private string LinkToSecret(string linkPath, string secretName, string content)
    {
        string secret = Path.Combine(outside, secretName);
        File.WriteAllText(secret, content);
        Directory.CreateDirectory(Path.GetDirectoryName(linkPath) ?? cwd);
        LinkFixtures.FileLink(linkPath, secret);
        return secret;
    }

    private static void AssertRefusal(RoleRenderException ex, string linkPath, string secretPath)
    {
        Assert.Contains("resolves outside the working directory", ex.Message, StringComparison.Ordinal);
        Assert.Contains(linkPath, ex.Message, pathComparison);
        Assert.Contains(secretPath, ex.Message, pathComparison);
        Assert.DoesNotContain(Secret, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalRoleJsonDeepMergesOverTheEmbeddedDefinition()
    {
        string roleDir = Path.Combine(cwd, ".claustrum", "roles", "builder");
        Directory.CreateDirectory(roleDir);
        File.WriteAllText(Path.Combine(roleDir, "role.json"), /*lang=json,strict*/ """
            {"description":"Overridden description","tiers":{"high":{"effort":"max"}},"deny":["custom-deny"]}
            """);

        RoleLibrary library = new();
        LoadedRole loaded = library.LoadRole("builder", cwd);

        Assert.True(loaded.IsLocalOverride);
        Assert.Equal("Overridden description", loaded.Definition.Description);
        Assert.Equal("frontier-coding", loaded.Definition.Tiers["high"].Model); // untouched by the merge
        Assert.Equal("max", loaded.Definition.Tiers["high"].Effort); // overridden
        Assert.Equal(["custom-deny"], loaded.Definition.Deny); // arrays replace wholesale, not concatenate
        Assert.Equal("edit+shell", loaded.Definition.Permission); // untouched
        Assert.Equal("xhigh", loaded.Definition.Tiers["xhigh"].Effort); // sibling tier untouched
    }

    [Fact]
    public void NoLocalOverrideUsesTheEmbeddedDefinitionUnchanged()
    {
        RoleLibrary library = new();
        LoadedRole loaded = library.LoadRole("builder", cwd);

        Assert.False(loaded.IsLocalOverride);
        Assert.Equal(["git push"], loaded.Definition.Deny);
    }

    // #54: a parts/ file is as much an override as ROLE.md, so it must earn the `source=local` marker.
    [Fact]
    public void ALocalPartAloneCountsAsALocalOverride()
    {
        string partsDir = Path.Combine(cwd, ".claustrum", "roles", "builder", "parts");
        Directory.CreateDirectory(partsDir);
        File.WriteAllText(Path.Combine(partsDir, "delegation.claude.md"), "local delegation");

        Assert.True(new RoleLibrary().LoadRole("builder", cwd).IsLocalOverride);
    }

    // #54: both used to escape as a bare JsonException, which the CLI printed as `error: …`, exit 1.
    [Theory]
    [InlineData("{ not json")]
    [InlineData(/*lang=json,strict*/ """{"blind":"yes"}""")]
    public void AMalformedLocalRoleJsonIsARenderErrorNamingTheFile(string content)
    {
        string roleDir = Path.Combine(cwd, ".claustrum", "roles", "builder");
        Directory.CreateDirectory(roleDir);
        string path = Path.Combine(roleDir, "role.json");
        File.WriteAllText(path, content);

        RoleRenderException ex = Assert.Throws<RoleRenderException>(() => new RoleLibrary().LoadRole("builder", cwd));

        Assert.Contains(path, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALinkedRoleMdLeavingTheWorkingDirectoryIsRefusedWithoutReadingIt()
    {
        string link = OverrideFile("ROLE.md");
        string secret = LinkToSecret(link, "secret.md", Secret);

        RoleRenderException ex = Assert.Throws<RoleRenderException>(() => new RoleLibrary().LoadRole("builder", cwd));

        AssertRefusal(ex, link, secret);
        Assert.Contains("role 'builder'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALinkedRoleJsonLeavingTheWorkingDirectoryIsRefusedWithoutReadingIt()
    {
        string link = OverrideFile("role.json");
        string secret = LinkToSecret(link, "secret.json", $$"""{"description":"{{Secret}}"}""");

        RoleRenderException ex = Assert.Throws<RoleRenderException>(() => new RoleLibrary().LoadRole("builder", cwd));

        AssertRefusal(ex, link, secret);
    }

    // The harness-specific name and the `.default.md` fallback are two different local files: each is fenced on its own.
    [Theory]
    [InlineData("delegation.claude.md")]
    [InlineData("delegation.default.md")]
    public void ALinkedPartLeavingTheWorkingDirectoryIsRefusedUnderBothOfItsNames(string file)
    {
        string link = OverrideFile("parts", file);
        string secret = LinkToSecret(link, "part.md", Secret);

        RoleRenderException ex = Assert.Throws<RoleRenderException>(() => new RoleLibrary().ReadPart("builder", "delegation", "claude", cwd));

        AssertRefusal(ex, link, secret);
    }

    // The renderer only forwards the cwd: a linked role.json is met by every entry point that loads the role, and a linked part
    // by both renders that expand `{{part:...}}` (the part is read only then).
    [Fact]
    public void TheRendererRefusesALinkedRoleJsonThroughEveryEntryPoint()
    {
        LinkToSecret(OverrideFile("role.json"), "role.json", $$"""{"description":"{{Secret}}"}""");
        RoleRenderer renderer = new(new RoleLibrary());

        Assert.Throws<RoleRenderException>(() => renderer.Render("builder", "high", "claude", cwd));
        Assert.Throws<RoleRenderException>(() => renderer.RenderTierStub("builder", "xhigh", "claude", cwd));
        Assert.Throws<RoleRenderException>(() => renderer.TierModelClass("builder", "high", cwd));
    }

    [Fact]
    public void TheRendererRefusesALinkedPartWhereverThePartIsExpanded()
    {
        string link = OverrideFile("parts", "delegation.claude.md");
        string secret = LinkToSecret(link, "part.md", Secret);
        RoleRenderer renderer = new(new RoleLibrary());

        RoleRenderException render = Assert.Throws<RoleRenderException>(() => renderer.Render("builder", "high", "claude", cwd));
        RoleRenderException stub = Assert.Throws<RoleRenderException>(() => renderer.RenderTierStub("builder", "xhigh", "claude", cwd));

        AssertRefusal(render, link, secret);
        AssertRefusal(stub, link, secret);
    }

    [Fact]
    public void ALinkedPartsDirectoryLeavingTheWorkingDirectoryIsRefusedWhenAPartIsReadAndLoadRoleReadsNothing()
    {
        string store = Directory.CreateDirectory(Path.Combine(outside, "parts-store")).FullName;
        File.WriteAllText(Path.Combine(store, "delegation.claude.md"), Secret);
        Directory.CreateDirectory(RoleDirectory);
        LinkFixtures.DirectoryLink(OverrideFile("parts"), store);

        LoadedRole loaded = new RoleLibrary().LoadRole("builder", cwd);
        RoleRenderException ex = Assert.Throws<RoleRenderException>(() => new RoleLibrary().ReadPart("builder", "delegation", "claude", cwd));

        Assert.True(loaded.IsLocalOverride);
        AssertRefusal(ex, OverrideFile("parts", "delegation.claude.md"), Path.Combine(store, "delegation.claude.md"));
    }

    [Fact]
    public void AClaustrumDirectoryLinkedOutsideAndHoldingAnOverrideIsRefused()
    {
        string held = Directory.CreateDirectory(Path.Combine(outside, "roles", "builder")).FullName;
        File.WriteAllText(Path.Combine(held, "ROLE.md"), Secret);
        LinkFixtures.DirectoryLink(Path.Combine(cwd, ".claustrum"), outside);

        RoleRenderException ex = Assert.Throws<RoleRenderException>(() => new RoleLibrary().LoadRole("builder", cwd));

        AssertRefusal(ex, OverrideFile("ROLE.md"), Path.Combine(held, "ROLE.md"));
    }

    // The fence acts on an override file that exists: a link out with nothing behind it is just a directory the role never uses.
    [Fact]
    public void AClaustrumDirectoryLinkedOutsideWithNoOverrideFileChangesNothing()
    {
        File.WriteAllText(Path.Combine(outside, "unrelated.txt"), Secret);
        LinkFixtures.DirectoryLink(Path.Combine(cwd, ".claustrum"), outside);
        RoleLibrary library = new();

        LoadedRole loaded = library.LoadRole("builder", cwd);
        LoadedRole embedded = library.LoadRole("builder", null);

        Assert.False(loaded.IsLocalOverride);
        Assert.Equal(embedded.RoleMdTemplate, loaded.RoleMdTemplate);
        Assert.Equal(embedded.Definition.Description, loaded.Definition.Description);
        Assert.False(RoleLibrary.HasLocalOverride("builder", cwd));
    }

    // File.Exists is true for a dangling link, so it is an override file like any other: refused when it leads out, and nothing
    // is created at its target.
    [Fact]
    public void ADanglingRoleMdLinkLeavingTheWorkingDirectoryIsRefusedAndCreatesNothingThroughIt()
    {
        Directory.CreateDirectory(RoleDirectory);
        string absent = Path.Combine(outside, "absent.md");
        LinkFixtures.FileLink(OverrideFile("ROLE.md"), absent);

        RoleRenderException ex = Assert.Throws<RoleRenderException>(() => new RoleLibrary().LoadRole("builder", cwd));

        Assert.Contains("resolves outside the working directory", ex.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(outside));
    }

    [Fact]
    public void ALinkedRoleMdThatStaysInsideTheWorkingDirectoryIsReadThroughAndCountsAsAnOverride()
    {
        string real = Directory.CreateDirectory(Path.Combine(cwd, "real")).FullName;
        File.WriteAllText(Path.Combine(real, "ROLE.md"), "You are the LINKED builder.\n");
        Directory.CreateDirectory(RoleDirectory);
        LinkFixtures.FileLink(OverrideFile("ROLE.md"), Path.Combine(real, "ROLE.md"));

        LoadedRole loaded = new RoleLibrary().LoadRole("builder", cwd);

        Assert.True(loaded.IsLocalOverride);
        Assert.Equal("You are the LINKED builder.\n", loaded.RoleMdTemplate);
    }

    [Fact]
    public void AClaustrumDirectoryLinkedInsideTheWorkingDirectoryIsReadThrough()
    {
        string store = Directory.CreateDirectory(Path.Combine(cwd, "dotfiles", "claustrum", "roles", "builder")).FullName;
        File.WriteAllText(Path.Combine(store, "ROLE.md"), "You are the STOWED builder.\n");
        File.WriteAllText(Path.Combine(store, "role.json"), /*lang=json,strict*/ """{"description":"Stowed description"}""");
        LinkFixtures.DirectoryLink(Path.Combine(cwd, ".claustrum"), Path.Combine("dotfiles", "claustrum"));

        LoadedRole loaded = new RoleLibrary().LoadRole("builder", cwd);

        Assert.True(loaded.IsLocalOverride);
        Assert.Equal("You are the STOWED builder.\n", loaded.RoleMdTemplate);
        Assert.Equal("Stowed description", loaded.Definition.Description);
    }

    // #99: `--global` hands the syncs a null cwd, and a null cwd must not look at the disk for overrides at all.
    [Fact]
    public void ANullWorkingDirectoryReadsTheLibraryAloneWhateverTheDiskHolds()
    {
        Directory.CreateDirectory(OverrideFile("parts"));
        File.WriteAllText(OverrideFile("ROLE.md"), "LOCAL-OVERRIDE-BODY\n");
        File.WriteAllText(OverrideFile("role.json"), /*lang=json,strict*/ """{"description":"Local builder"}""");
        File.WriteAllText(OverrideFile("parts", "delegation.claude.md"), "LOCAL-PART");
        RoleLibrary library = new();
        RoleRenderer renderer = new(library);

        LoadedRole alone = library.LoadRole("builder", null);
        LoadedRole local = library.LoadRole("builder", cwd);

        Assert.True(local.IsLocalOverride);
        Assert.False(alone.IsLocalOverride);
        Assert.NotEqual("Local builder", alone.Definition.Description);
        Assert.DoesNotContain("LOCAL-OVERRIDE-BODY", alone.RoleMdTemplate, StringComparison.Ordinal);
        string part = library.ReadPart("builder", "delegation", "claude", null);
        Assert.DoesNotContain("LOCAL-PART", part, StringComparison.Ordinal);
        Assert.Equal(library.ReadPart("builder", "delegation", "claude", outside), part);
        Assert.Equal("LOCAL-PART", library.ReadPart("builder", "delegation", "claude", cwd));
        Assert.DoesNotContain("LOCAL-OVERRIDE-BODY", renderer.Render("builder", "high", "claude", null).SystemBody, StringComparison.Ordinal);
        Assert.DoesNotContain("LOCAL-PART", renderer.RenderTierStub("builder", "xhigh", "claude", null), StringComparison.Ordinal);
        Assert.Equal(library.LoadRole("builder", outside).Definition.Tiers["high"].Model, renderer.TierModelClass("builder", "high", null));
    }

    // Existence only: it is the probe `sync --global` runs over a repo it must not read, so a link out is "something is there".
    [Fact]
    public void HasLocalOverrideIsStaticExistenceOnlyAndDoesNotReadALinkThatLeavesTheWorkingDirectory()
    {
        Assert.False(RoleLibrary.HasLocalOverride("builder", cwd));
        LinkToSecret(OverrideFile("ROLE.md"), "secret.md", Secret);

        Assert.True(RoleLibrary.HasLocalOverride("builder", cwd));
    }

    [Theory]
    [InlineData("role.json")]
    [InlineData("ROLE.md")]
    [InlineData("parts/delegation.claude.md")]
    public void HasLocalOverrideSeesEachKindOfOverrideFile(string relative)
    {
        string file = OverrideFile(relative.Split('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(file) ?? cwd);
        File.WriteAllText(file, "x");

        Assert.True(RoleLibrary.HasLocalOverride("builder", cwd));
        Assert.False(RoleLibrary.HasLocalOverride("tester", cwd));
    }

    // 2026-10-13 (#99 review): a probe that throws would abort `sync --global` over a repo whose `parts/` cannot be listed.
    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void HasLocalOverrideIsTrueWhenThePartsDirectoryCannotBeListedOnUnix()
    {
        string parts = Directory.CreateDirectory(OverrideFile("parts")).FullName;
        using IDisposable locked = LinkFixtures.Lock(parts);

        Assert.True(RoleLibrary.HasLocalOverride("builder", cwd));
        Assert.True(new RoleLibrary().LoadRole("builder", cwd).IsLocalOverride);
    }
}
