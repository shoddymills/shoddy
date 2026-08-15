using Fettler.Cli;
using Fettler.Core;
using Xunit;

namespace Fettler.Tests;

/// <summary>
/// R8.9: where the boundary comes from when the command line does not
/// say it.
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

        public string Config(string at, string json)
        {
            string dir = at.Length == 0 ? Root : Dir(at);
            string file = Path.Combine(dir, RootsFile.FileName);
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
        string file = tree.Config("", """{ "roots": { "repo": "." } }""");
        string deep = tree.Dir("src/inner/deeper");

        Result<RootsFile.Found> found = RootsFile.Discover(deep);

        Assert.True(found.IsOk, found.Failure?.Message);
        Assert.Equal(file, found.Value.File);
        Assert.Equal("repo", found.Value.Roots[0].Name);
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
        tree.Config("", """{ "roots": { "repo": "sub" } }""");
        string wanted = tree.Dir("sub");
        string asking = tree.Dir("src/inner");

        Result<RootsFile.Found> found = RootsFile.Discover(asking);

        Assert.True(found.IsOk, found.Failure?.Message);
        Assert.Equal(wanted, found.Value.Roots[0].Path);
    }

    [Fact]
    public void TheNEARESTConfigurationWinsRatherThanTheTopmost()
    {
        using var tree = new Tree();
        tree.Config("", """{ "roots": { "outer": "." } }""");
        string inner = tree.Config("nested", """{ "roots": { "inner": "." } }""");

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Dir("nested/further"));

        Assert.True(found.IsOk, found.Failure?.Message);
        Assert.Equal(inner, found.Value.File);
        Assert.Equal("inner", found.Value.Roots[0].Name);
    }

    /// <summary>
    /// The one that matters. Falling back to the current directory on a
    /// broken configuration would usually WIDEN the boundary, silently,
    /// because a caller's cwd tends to sit above the root they meant.
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
    [InlineData("""{ "roots": { } }""")]
    [InlineData("""{ "nothing": true }""")]
    [InlineData("""{ "roots": { "repo": 3 } }""")]
    [InlineData("""{ "roots": { "repo": "" } }""")]
    [InlineData("""[ "roots" ]""")]
    public void AConfigurationThatDeclaresNoUsableRootIsRefused(string json)
    {
        using var tree = new Tree();
        tree.Config("", json);

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);

        Assert.False(found.IsOk);
        Assert.Equal(Outcome.Invalid, found.Failure!.Outcome);
    }

    /// <summary>NotFound is the ordinary case and the ONLY one the
    /// caller may fall back from, so it is distinguished by outcome
    /// rather than by reading the message.</summary>
    [Fact]
    public void NoConfigurationAnywhereIsNotFoundSoTheCallerMayFallBack()
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
        tree.Config("", $$"""{ "roots": { "far": {{System.Text.Json.JsonSerializer.Serialize(other.Root)}} } }""");

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);

        Assert.True(found.IsOk, found.Failure?.Message);
        Assert.Equal(other.Root, found.Value.Roots[0].Path);
    }

    /// <summary>
    /// R8.9: --root REPLACES the configuration. Merging would leave a
    /// caller unable to narrow the boundary - the tree's own roots would
    /// stay open beside whatever they asked for.
    /// </summary>
    [Fact]
    public void TheCommandLineReplacesTheConfigurationRatherThanAddingToIt()
    {
        using var tree = new Tree();
        string file = tree.Config("", """{ "roots": { "repo": "." } }""");
        string only = tree.Dir("sub");

        Arguments args = Arguments.Parse(["roots", "--config", file, "--root", $"just={only}"]);
        Result<Roots> opened = args.DeclaredRoots();

        Assert.True(opened.IsOk, opened.Failure?.Message);
        Assert.Equal(["just"], opened.Value.Names);
        Assert.Equal("the command line", opened.Value.Origin);
    }

    [Fact]
    public void ConfigNamedByFlagIsRead_AndTheRootsSayWhichFileSaidSo()
    {
        using var tree = new Tree();
        string file = tree.Config("", """{ "roots": { "repo": ".", "sub": "sub" } }""");
        tree.Dir("sub");

        Arguments args = Arguments.Parse(["roots", "--config", file]);
        Result<Roots> opened = args.DeclaredRoots();

        Assert.True(opened.IsOk, opened.Failure?.Message);
        Assert.Equal(["repo", "sub"], opened.Value.Names);
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

    [Fact]
    public void NoConfigTurnsTheSearchOffEntirely()
    {
        Arguments args = Arguments.Parse(["roots", "--no-config"]);
        Result<Roots> opened = args.DeclaredRoots();

        Assert.True(opened.IsOk, opened.Failure?.Message);
        Assert.Equal(["root"], opened.Value.Names);
        Assert.Contains("current directory", opened.Value.Origin);
    }
}
