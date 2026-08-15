using Fettler.Cli;
using Fettler.Core;
using Xunit;

namespace Fettler.Tests;

/// <summary>
/// R8.9 and B.2: where the boundary comes from when the command line does
/// not say it, and what it grants when it does.
///
/// <para>These tests take the directory to search from as an argument
/// rather than setting the process's current directory, because the
/// suite runs concurrently and a current directory is process-wide -
/// one test changing it would move the ground under every other test
/// at once, intermittently.</para>
/// </summary>
public sealed class ConfigTests
{
    /// <summary>A throwaway tree, resolved the way Roots resolves one,
    /// so a comparison holds on macOS where the temp directory is
    /// itself a link.</summary>
    sealed class Tree : IDisposable
    {
        public Tree() => Root = Real(Directory.CreateTempSubdirectory("fettle-config-").FullName);

        public string Root { get; }

        public string Dir(string relative)
        {
            string full = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(full);
            return full;
        }

        public string Config(string at, string json) => Put(at, RootsFile.FileName, json);

        public string Local(string at, string json) => Put(at, RootsFile.LocalFileName, json);

        string Put(string at, string name, string json)
        {
            string dir = at.Length == 0 ? Root : Dir(at);
            string file = Path.Combine(dir, name);
            File.WriteAllText(file, json);
            return file;
        }

        public static string Real(string path) =>
            Directory.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void DiscoveryWalksUpFromASubdirectoryToTheTreeItIsIn()
    {
        using var tree = new Tree();
        string file = tree.Config("", """{ "trees": { "work": { "path": "." } } }""");
        string deep = tree.Dir("src/inner/deeper");

        Result<RootsFile.Found> found = RootsFile.Discover(deep);

        Assert.True(found.IsOk, found.Failure?.Message);
        Assert.Equal(file, found.Value.File);
        Assert.Equal("work", found.Value.Trees[0].Name);
    }

    /// <summary>
    /// The reason the file exists at all. A path read against the
    /// caller's current directory names a different tree from every
    /// directory; read against the file's own directory it names one
    /// tree from everywhere.
    /// </summary>
    [Fact]
    public void PathsAreRelativeToTheFileAndNotToWhoeverIsAsking()
    {
        using var tree = new Tree();
        tree.Config("", """{ "trees": { "work": { "path": "sub" } } }""");
        string wanted = tree.Dir("sub");
        string asking = tree.Dir("src/inner");

        Result<RootsFile.Found> found = RootsFile.Discover(asking);

        Assert.True(found.IsOk, found.Failure?.Message);
        Assert.Equal(wanted, found.Value.Trees[0].Path);
    }

    [Fact]
    public void TheNEARESTConfigurationWinsRatherThanTheTopmost()
    {
        using var tree = new Tree();
        tree.Config("", """{ "trees": { "outer": { "path": "." } } }""");
        string inner = tree.Config("nested", """{ "trees": { "inner": { "path": "." } } }""");

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Dir("nested/further"));

        Assert.True(found.IsOk, found.Failure?.Message);
        Assert.Equal(inner, found.Value.File);
        Assert.Equal("inner", found.Value.Trees[0].Name);
    }

    /// <summary>
    /// The one that matters. Falling back to the current directory on a
    /// broken configuration would usually WIDEN the boundary, silently,
    /// because a caller's cwd tends to sit above the tree they meant.
    /// </summary>
    [Fact]
    public void AMalformedConfigurationIsRefusedAndNeverFallenBackFrom()
    {
        using var tree = new Tree();
        tree.Config("", "{ this is not json");

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Dir("src"));

        Assert.False(found.IsOk);
        Assert.Equal(Outcome.Invalid, found.Failure!.Outcome);
        Assert.NotEqual(Outcome.NotFound, found.Failure!.Outcome);
        Assert.Contains("JSON", found.Failure!.Message);
    }

    [Theory]
    [InlineData("""{ "trees": { } }""")]
    [InlineData("""{ "nothing": true }""")]
    [InlineData("""{ "trees": { "work": 3 } }""")]
    [InlineData("""{ "trees": { "work": { } } }""")]
    [InlineData("""{ "trees": { "work": { "path": "" } } }""")]
    [InlineData("""{ "trees": { "work": { "path": ".", "can": "read" } } }""")]
    [InlineData("""{ "trees": { "work": { "path": ".", "can": ["delte"] } } }""")]
    [InlineData("""{ "trees": { "work": { "path": ".", "scopes": [] } } }""")]
    [InlineData("""{ "trees": { "work": { "path": ".", "scopes": { "sub": { } } } } }""")]
    [InlineData("""[ "trees" ]""")]
    public void AConfigurationThatDeclaresNoUsableTreeIsRefused(string json)
    {
        using var tree = new Tree();
        tree.Config("", json);

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);

        Assert.False(found.IsOk);
        Assert.Equal(Outcome.Invalid, found.Failure!.Outcome);
    }

    /// <summary>
    /// B.2: the old shape meant something different - a root was
    /// readable and writable in full - so reading it as the new one would
    /// GRANT more than the file says. Refused, with the whole of what to
    /// do rather than a complaint.
    /// </summary>
    [Fact]
    public void TheOldRootsShapeIsRefusedWithTheMigrationNamed()
    {
        using var tree = new Tree();
        tree.Config("", """{ "roots": { "repo": "." } }""");

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);

        Assert.False(found.IsOk);
        Assert.Equal(Outcome.Invalid, found.Failure!.Outcome);
        Assert.Contains("\"trees\"", found.Failure.Message);
        Assert.Contains("execute is never granted by default", found.Failure.Message);
    }

    [Fact]
    public void ATreeWrittenAsABarePathSaysWhatToWriteInstead()
    {
        using var tree = new Tree();
        tree.Config("", """{ "trees": { "work": "." } }""");

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);

        Assert.False(found.IsOk);
        Assert.Contains("\"path\"", found.Failure!.Message);
    }

    /// <summary>NotFound is the ordinary case and the ONLY one the
    /// caller may fall back from, so it is distinguished by outcome
    /// rather than by reading the message.</summary>
    [Fact]
    public void NoConfigurationAnywhereIsNotFound()
    {
        using var tree = new Tree();

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Dir("src/inner"));

        Assert.False(found.IsOk);
        Assert.Equal(Outcome.NotFound, found.Failure!.Outcome);
    }

    [Fact]
    public void AnAbsolutePathInTheConfigurationIsLeftAbsolute()
    {
        using var tree = new Tree();
        using var other = new Tree();
        tree.Config("", $$"""
            { "trees": { "far": { "path": {{System.Text.Json.JsonSerializer.Serialize(other.Root)}} } } }
            """);

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);

        Assert.True(found.IsOk, found.Failure?.Message);
        Assert.Equal(other.Root, found.Value.Trees[0].Path);
    }

    // ---- the defaults, which are the safe ones ----

    /// <summary>
    /// The tree the configuration sits in gets everything but execute;
    /// every other tree gets list read. Deciding by WHERE the tree is
    /// rather than by how the path was spelled means "./" and an absolute
    /// path to the same folder mean the same thing.
    /// </summary>
    [Fact]
    public void TheTreeTheConfigurationSitsInIsWritableAndEveryOtherOneIsNot()
    {
        using var tree = new Tree();
        using var other = new Tree();
        tree.Config("", $$"""
            {
              "trees": {
                "work": { "path": "." },
                "here": { "path": {{System.Text.Json.JsonSerializer.Serialize(tree.Root)}} },
                "far":  { "path": {{System.Text.Json.JsonSerializer.Serialize(other.Root)}} }
              }
            }
            """);

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);
        Assert.True(found.IsOk, found.Failure?.Message);

        Assert.Equal(Permissions.Full, found.Value.Trees[0].Can);
        Assert.Equal(Permissions.Full, found.Value.Trees[1].Can);
        Assert.Equal(Permissions.ReadOnly, found.Value.Trees[2].Can);
    }

    [Fact]
    public void ExecuteIsNeverADefaultAnywhere()
    {
        Assert.False(Permissions.Full.HasFlag(Permission.Execute));
        Assert.False(Permissions.ReadOnly.HasFlag(Permission.Execute));
    }

    [Fact]
    public void ScopesAreReadAndMayGrantNothingAtAll()
    {
        using var tree = new Tree();
        tree.Dir("requirements/cancelled");
        tree.Config("", """
            {
              "trees": {
                "work": {
                  "path": ".",
                  "can": ["list", "read"],
                  "scopes": {
                    "requirements":           { "can": ["list", "read", "create", "update", "rename"] },
                    "requirements/cancelled": { "can": [] }
                  }
                }
              }
            }
            """);

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);
        Assert.True(found.IsOk, found.Failure?.Message);

        TreeDecl work = found.Value.Trees[0];
        Assert.Equal(Permissions.ReadOnly, work.Can);
        Assert.Equal(2, work.Scopes!.Count);
        Assert.Equal(Permission.None, work.Scopes[1].Can);
        Assert.True(work.Scopes[0].Can.HasFlag(Permission.Rename));
        Assert.False(work.Scopes[0].Can.HasFlag(Permission.Delete));
    }

    [Fact]
    public void AScopeOutsideItsOwnTreeIsAConfigurationMistake()
    {
        using var tree = new Tree();
        tree.Config("", """
            { "trees": { "work": { "path": ".", "scopes": { "../elsewhere": { "can": [] } } } } }
            """);

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);
        Assert.True(found.IsOk, found.Failure?.Message);

        Result<Roots> opened = Roots.Open(found.Value.Trees);
        Assert.False(opened.IsOk);
        Assert.Contains("not inside", opened.Failure!.Message);
    }

    // ---- the personal overlay (B.12) ----

    [Fact]
    public void TheLocalOverlayAddsTreesAndReplacesOnesOfTheSameName()
    {
        using var tree = new Tree();
        using var mine = new Tree();
        tree.Config("", """{ "trees": { "work": { "path": ".", "can": ["list"] } } }""");
        tree.Local("", $$"""
            {
              "trees": {
                "work": { "path": ".", "can": ["list", "read"] },
                "mine": { "path": {{System.Text.Json.JsonSerializer.Serialize(mine.Root)}} }
              }
            }
            """);

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);

        Assert.True(found.IsOk, found.Failure?.Message);
        Assert.Equal(["work", "mine"], found.Value.Trees.Select(t => t.Name));
        Assert.Equal(Permissions.ReadOnly, found.Value.Trees[0].Can);
        Assert.Contains(RootsFile.LocalFileName, found.Value.Origin);
    }

    [Fact]
    public void ABrokenOverlayIsRefusedRatherThanIgnored()
    {
        using var tree = new Tree();
        tree.Config("", """{ "trees": { "work": { "path": "." } } }""");
        tree.Local("", "{ not json");

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);

        Assert.False(found.IsOk);
        Assert.Equal(Outcome.Invalid, found.Failure!.Outcome);
    }

    // ---- where a permission may come from ----

    /// <summary>
    /// R8.9: --root REPLACES the configuration. Merging would leave a
    /// caller unable to narrow the boundary - the tree's own trees would
    /// stay open beside whatever they asked for.
    /// </summary>
    [Fact]
    public void TheCommandLineReplacesTheConfigurationRatherThanAddingToIt()
    {
        using var tree = new Tree();
        string file = tree.Config("", """{ "trees": { "work": { "path": "." } } }""");
        string only = tree.Dir("sub");

        Arguments args = Arguments.Parse(["roots", "--config", file, "--root", $"just={only}"]);
        Result<Roots> opened = args.DeclaredRoots();

        Assert.True(opened.IsOk, opened.Failure?.Message);
        Assert.Equal(["just"], opened.Value.Names);
    }

    /// <summary>
    /// B.4, and the hole it closes. A command line is not a deliberate,
    /// reviewable act inside the tree it governs - it is one line
    /// somebody typed, or one line in a client configuration a model
    /// never saw. It may open a tree for reading and it may not do more,
    /// whatever combination of flags is offered.
    /// </summary>
    [Fact]
    public void ARootOnTheCommandLineGrantsListAndReadAndNothingElse()
    {
        using var tree = new Tree();

        foreach (string[] argv in new[]
        {
            new[] { "roots", "--root", tree.Root },
            ["roots", "--root", $"named={tree.Root}"],
            ["roots", "--root", tree.Root, "--root", $"second={tree.Root}"],
            ["roots", "--no-config", "--root", tree.Root],
        })
        {
            Result<Roots> opened = Arguments.Parse(argv).DeclaredRoots();
            Assert.True(opened.IsOk, opened.Failure?.Message);

            foreach (string name in opened.Value.Names)
                Assert.Equal(Permissions.ReadOnly, opened.Value.GrantOf(name).Can);
        }
    }

    [Fact]
    public void ConfigNamedByFlagIsRead_AndTheRootsSayWhichFileSaidSo()
    {
        using var tree = new Tree();
        string file = tree.Config("", """
            { "trees": { "work": { "path": "." }, "sub": { "path": "sub" } } }
            """);
        tree.Dir("sub");

        Arguments args = Arguments.Parse(["roots", "--config", file]);
        Result<Roots> opened = args.DeclaredRoots();

        Assert.True(opened.IsOk, opened.Failure?.Message);
        Assert.Equal(["work", "sub"], opened.Value.Names);
        Assert.Equal(file, opened.Value.Origin);
    }

    [Fact]
    public void AConfigNamedByFlagThatIsNotThereIsRefusedRatherThanIgnored()
    {
        using var tree = new Tree();

        Arguments args = Arguments.Parse(
            ["roots", "--config", Path.Combine(tree.Root, "absent.json")]);
        Result<Roots> opened = args.DeclaredRoots();

        Assert.False(opened.IsOk);
        Assert.Equal(Outcome.NotFound, opened.Failure!.Outcome);
    }

    /// <summary>
    /// B.3. The current directory used to become a tree when nothing was
    /// declared, and that is the "searching around" the boundary exists
    /// to stop: a cwd sits ABOVE the tree somebody meant, so the fallback
    /// widened the boundary at exactly the moment nobody had stated one.
    /// </summary>
    [Fact]
    public void DeclaringNothingIsARefusalThatSaysHowToDeclareSomething()
    {
        Arguments args = Arguments.Parse(["roots", "--no-config"]);
        Result<Roots> opened = args.DeclaredRoots();

        Assert.False(opened.IsOk);
        Assert.Equal(Outcome.Invalid, opened.Failure!.Outcome);
        Assert.Contains(RootsFile.FileName, opened.Failure.Message);
        Assert.Contains("--root", opened.Failure.Message);
    }
}
