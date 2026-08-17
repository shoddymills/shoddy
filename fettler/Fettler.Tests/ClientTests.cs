using System.Text.Json;
using Fettler.Clients;
using Fettler.Core;
using Xunit;

namespace Fettler.Tests;

/// <summary>
/// Phase 6 for the doctor and the scaffolder, which read and write other
/// programs' configuration and are therefore the highest-consequence code
/// in the tool.
///
/// <para><b>6.1 governs every test here: no test may read the real
/// machine's configuration.</b> A suite that does is a suite that passes
/// only on the machine it was written on, and this is exactly the code
/// where that would matter most. Every path comes from a fixture
/// <see cref="Machine"/> - a synthetic home, a synthetic app-data and a
/// synthetic tree - which is why <c>Machine</c> takes its three
/// directories as arguments rather than reading the environment.</para>
/// </summary>
public sealed class ClientTests
{
    /// <summary>A whole machine in a temporary directory: a home, an
    /// application-data folder and a tree, none of them real.</summary>
    sealed class Fixture : IDisposable
    {
        readonly string root = Directory.CreateTempSubdirectory("fettle-machine-").FullName;

        public Fixture()
        {
            Machine = new Machine(Dir("home"), Dir("appdata"), Dir("tree"));
        }

        public Machine Machine { get; }

        public string Dir(string relative)
        {
            string full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(full);
            return full;
        }

        /// <summary>Put a file at one of the places, so a test names the
        /// PURPOSE rather than repeating a path the code already
        /// knows.</summary>
        public string Put(string client, Level level, Purpose purpose, string content)
        {
            Place place = Places.All(Machine).First(p =>
                p.Client == client && p.Level == level && p.Purpose == purpose);

            Directory.CreateDirectory(Path.GetDirectoryName(place.Path)!);
            File.WriteAllText(place.Path, content);
            return place.Path;
        }

        public string PathOf(string client, Level level, Purpose purpose) =>
            Places.All(Machine).First(p => p.Client == client && p.Level == level && p.Purpose == purpose).Path;

        public void Dispose()
        {
            try { Directory.Delete(root, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    static Diagnosis Examine(Fixture box) => Doctor.Examine(box.Machine, null, "test");

    static ClientReport Report(Diagnosis d, string client, Level level) =>
        d.Clients.First(c => c.Client == client && c.Level == level);

    // ---- 6.2: all four verdicts, per client ----

    /// <summary>
    /// The distinction the verdicts exist to make. Claude Desktop has no
    /// project scope at all - reporting that as a failure would train a
    /// reader to ignore the whole report, which is how a diagnostic stops
    /// being read.
    /// </summary>
    [Fact]
    public void AClientWithNothingAtALevelIsNotApplicableAndNeverAFailure()
    {
        using var box = new Fixture();

        Diagnosis d = Examine(box);

        Assert.Equal(Verdict.NotApplicable, Report(d, Places.ClaudeDesktop, Level.Repo).Verdict);
        Assert.Contains("no project scope", Report(d, Places.ClaudeDesktop, Level.Repo).Detail);
        Assert.False(d.AnyBroken);
    }

    [Fact]
    public void AClientThatIsHereWithoutFettlerRegisteredIsAbsent()
    {
        using var box = new Fixture();
        box.Put(Places.ClaudeCode, Level.Repo, Purpose.Servers, """{"mcpServers":{"other":{"command":"x"}}}""");

        Assert.Equal(Verdict.Absent, Report(Examine(box), Places.ClaudeCode, Level.Repo).Verdict);
    }

    /// <summary>
    /// Live on this machine when the works order was written: every
    /// documented registration launched a bare <c>fettle</c>, and nothing
    /// resolved it. A server that cannot start reports as registered from
    /// every direction but the one that matters.
    /// </summary>
    [Fact]
    public void ARegistrationWhoseCommandCannotBeLaunchedIsBroken()
    {
        using var box = new Fixture();
        box.Put(Places.ClaudeCode, Level.Repo, Purpose.Servers, """
            {"mcpServers":{"fettler":{"command":"a-binary-that-is-not-anywhere","args":["serve"]}}}
            """);

        ClientReport report = Report(Examine(box), Places.ClaudeCode, Level.Repo);

        Assert.Equal(Verdict.Broken, report.Verdict);
        Assert.Contains("resolves to nothing", report.Detail);
        Assert.True(Examine(box).AnyBroken);
    }

    [Fact]
    public void ARegistrationNamingARealBinaryIsHealthy()
    {
        using var box = new Fixture();
        string binary = Environment.ProcessPath!;
        box.Put(Places.ClaudeCode, Level.Repo, Purpose.Servers,
            "{\"mcpServers\":{\"fettler\":{\"command\":" + JsonSerializer.Serialize(binary) + ",\"args\":[\"serve\"]}}}");

        Assert.Equal(Verdict.Healthy, Report(Examine(box), Places.ClaudeCode, Level.Repo).Verdict);
    }

    [Fact]
    public void VsCodeKeepsItsServersUnderADifferentKeyAndIsFoundThere()
    {
        using var box = new Fixture();
        box.Put(Places.VsCodeCopilot, Level.Repo, Purpose.Servers,
            "{\"servers\":{\"fettler\":{\"command\":" + JsonSerializer.Serialize(Environment.ProcessPath!) + "}}}");

        Assert.Equal(Verdict.Healthy, Report(Examine(box), Places.VsCodeCopilot, Level.Repo).Verdict);
    }

    [Fact]
    public void AConfigurationThatWillNotParseIsBrokenAndNeverSilentlySkipped()
    {
        using var box = new Fixture();
        box.Put(Places.ClaudeCode, Level.Global, Purpose.Servers, "{ this is not json");

        ClientReport report = Report(Examine(box), Places.ClaudeCode, Level.Global);

        Assert.Equal(Verdict.Broken, report.Verdict);
        Assert.Contains("will not parse", report.Detail);
    }

    // ---- 6.3: every override check, positive and clean ----

    [Fact]
    public void AShellCommandThatWritesFilesIsReportedAsAPreApprovedRouteRound()
    {
        using var box = new Fixture();
        box.Put(Places.ClaudeCode, Level.Global, Purpose.Policy, """
            {"permissions":{"allow":["Bash(sed -i:*)","Bash(cp:*)","PowerShell(Set-Content:*)","Bash(git status)"]}}
            """);

        Finding f = Examine(box).Findings.First(x => x.Check == "2.6");

        Assert.True(f.Serious);
        Assert.Contains("without a prompt", f.Message);
        Assert.Contains("sed", f.Message);

        // The one that reads rather than writes is not named.
        Assert.DoesNotContain("git status", f.Message);
    }

    /// <summary>
    /// Windows has TWO shell tools, not one. The machine that produced
    /// this design named Bash twenty-two times and PowerShell zero -
    /// while PowerShell was the shell used for essentially every command.
    /// A Bash-only check would have caught none of it.
    /// </summary>
    [Fact]
    public void PowerShellIsCheckedAsWellAsBash()
    {
        using var box = new Fixture();
        box.Put(Places.ClaudeCode, Level.Global, Purpose.Policy, """
            {"permissions":{"allow":["PowerShell(Remove-Item:*)"]}}
            """);

        Assert.Contains(Examine(box).Findings, f => f.Check == "2.6" && f.Message.Contains("Remove-Item"));
    }

    /// <summary>
    /// Read was the one first waved through, on the reasoning that
    /// reading is not the risk. It is: a blanket Read defeats
    /// containment, the read permission and hidden scopes at once.
    /// </summary>
    [Fact]
    public void AnAllowOnTheBuiltInReaderOrEditorsIsSerious()
    {
        using var box = new Fixture();
        box.Put(Places.ClaudeCode, Level.Global, Purpose.Policy, """
            {"permissions":{"allow":["Read(//c/somewhere/**)","Edit"]}}
            """);

        Finding f = Examine(box).Findings.First(x => x.Check == "2.7");

        Assert.True(f.Serious);
        Assert.Contains("Read", f.Message);
        Assert.Contains("hidden scopes", f.Message);
    }

    [Fact]
    public void NothingDenyingTheBuiltInEditorsIsNotedAtTheRepoLevel()
    {
        using var box = new Fixture();
        box.Put(Places.ClaudeCode, Level.Repo, Purpose.Policy, """{"permissions":{"allow":[]}}""");

        Finding f = Examine(box).Findings.First(x => x.Check == "2.8");

        Assert.False(f.Serious);
        Assert.Contains("an option", f.Message);
    }

    /// <summary>The six replaced tools alone are not the whole list: a
    /// shell left open is a route round the boundary, and
    /// <c>node x.js</c> takes it without being a writing command.</summary>
    [Fact]
    public void AnOpenShellIsReportedEvenWhenEveryReplacedToolIsDenied()
    {
        using var box = new Fixture();
        box.Put(Places.ClaudeCode, Level.Repo, Purpose.Policy, """
            {"permissions":{"deny":["Read","Write","Edit","NotebookEdit","Grep","Glob"]}}
            """);

        Finding f = Examine(box).Findings.First(x => x.Check == "2.8");

        Assert.Contains("Bash", f.Message);
        Assert.Contains("PowerShell", f.Message);
    }

    [Fact]
    public void DenyingThemAllLeavesNothingToReport()
    {
        using var box = new Fixture();
        box.Put(Places.ClaudeCode, Level.Repo, Purpose.Policy, """
            {"permissions":{"deny":["Read","Write","Edit","NotebookEdit","Grep","Glob","Bash","PowerShell"]}}
            """);

        Assert.DoesNotContain(Examine(box).Findings, f => f.Check is "2.7" or "2.8");
    }

    /// <summary>B.17 must not fire on the configuration this tool now
    /// writes. Denying the shell and allowing back a named command is the
    /// intended shape; calling it a conflict would name "remove the
    /// allow" as the fix, which is the one change that breaks it.</summary>
    [Fact]
    public void ANarrowedShellAllowBesideTheShellDenyIsNotAConflict()
    {
        using var box = new Fixture();
        box.Put(Places.ClaudeCode, Level.Repo, Purpose.Policy, """
            {"permissions":{"allow":["Bash(git status:*)"],"deny":["Read","Write","Edit","NotebookEdit","Grep","Glob","Bash","PowerShell"]}}
            """);

        Assert.DoesNotContain(Examine(box).Findings, f => f.Check == "B.17");
    }

    /// <summary>The exemption is for the shell and stops there: a
    /// narrowed allow on Read is still a hole, because reading is what
    /// Fettler does.</summary>
    [Fact]
    public void ANarrowedAllowOnAReplacedToolIsStillAConflict()
    {
        using var box = new Fixture();
        box.Put(Places.ClaudeCode, Level.Repo, Purpose.Policy, """
            {"permissions":{"allow":["Read(//c/x/**)"],"deny":["Read"]}}
            """);

        Assert.Contains(Examine(box).Findings, f => f.Check == "B.17");
    }

    /// <summary>
    /// B.17. Which of allow and deny wins is the client's to decide and
    /// cannot be observed from here, so the conflict is reported and the
    /// fix named is the one that is right under either precedence.
    /// </summary>
    [Fact]
    public void ARuleInBothAllowAndDenyIsReportedWithoutGuessingWhichWins()
    {
        using var box = new Fixture();
        box.Put(Places.ClaudeCode, Level.Global, Purpose.Policy, """
            {"permissions":{"allow":["Read(//c/x/**)"],"deny":["Read"]}}
            """);

        Finding f = Examine(box).Findings.First(x => x.Check == "B.17");

        Assert.True(f.Serious);
        Assert.Contains("cannot be determined from here", f.Message);
        Assert.Contains("remove the allow", f.Message);
    }

    /// <summary>
    /// Two boundaries that disagree means the narrower one is decorative,
    /// and the wider one is the one that applies.
    /// </summary>
    [Fact]
    public void AdditionalDirectoriesWiderThanTheDeclaredTreesIsReported()
    {
        using var box = new Fixture();
        string elsewhere = box.Dir("elsewhere");
        box.Put(Places.ClaudeCode, Level.Global, Purpose.Policy,
            $$$"""{"permissions":{"additionalDirectories":[{{{JsonSerializer.Serialize(elsewhere)}}}]}}""");

        Result<Roots> roots = Roots.Open([new TreeDecl("work", box.Machine.Tree, Permissions.Full)]);
        Diagnosis d = Doctor.Examine(box.Machine, roots.Value, "test");

        Assert.Contains(d.Findings, f => f.Check == "2.9" && f.Message.Contains("outside"));
    }

    /// <summary>
    /// Live on this machine: two project entries differing only in a
    /// drive letter's case. Approvals attach to whichever spelling was
    /// resolved that day and silently do not apply to the other.
    /// </summary>
    [Fact]
    public void TwoProjectKeysNamingOneDirectoryAreReported()
    {
        using var box = new Fixture();
        box.Put(Places.ClaudeCode, Level.Global, Purpose.Servers, """
            {"projects":{"c:/github/shoddy":{},"C:/github/shoddy":{}}}
            """);

        Assert.Contains(Examine(box).Findings, f => f.Check == "2.11");
    }

    [Fact]
    public void OneProjectKeySpelledOnceIsNotReported()
    {
        using var box = new Fixture();
        box.Put(Places.ClaudeCode, Level.Global, Purpose.Servers, """
            {"projects":{"c:/github/shoddy":{},"c:/github/other":{}}}
            """);

        Assert.DoesNotContain(Examine(box).Findings, f => f.Check == "2.11");
    }

    [Fact]
    public void InstructionsThatNeverMentionFettlerAreNoted()
    {
        using var box = new Fixture();
        box.Put(Places.ClaudeCode, Level.Repo, Purpose.Instructions, "# Working here\n\nBe careful.\n");

        Assert.Contains(Examine(box).Findings, f => f.Check == "2.10");
    }

    [Fact]
    public void APersonalSettingsFileIsNotedBecauseNobodyElseCanSeeIt()
    {
        using var box = new Fixture();
        string local = box.PathOf(Places.ClaudeCode, Level.Repo, Purpose.Policy)
            .Replace("settings.json", "settings.local.json");
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        File.WriteAllText(local, "{}");

        Assert.Contains(Examine(box).Findings, f => f.Check == "2.13");
    }

    [Fact]
    public void AutoApprovingArbitraryToolsIsSerious()
    {
        using var box = new Fixture();
        box.Put(Places.VsCodeCopilot, Level.Global, Purpose.Policy, """{"chat.tools.autoApprove":true}""");

        Assert.Contains(Examine(box).Findings, f => f.Check == "2.14" && f.Serious);
    }

    // ---- 6.7: no secret is ever printed ----

    /// <summary>
    /// <c>~/.claude/.credentials.json</c> sits in the same directory as
    /// files the doctor reads. It names files and keys, never values -
    /// and this asserts it by putting a distinctive secret on the machine
    /// and checking that nothing anywhere in the answer carries it.
    /// </summary>
    [Fact]
    public void NothingFromACredentialsFileReachesTheAnswer()
    {
        using var box = new Fixture();
        const string secret = "sk-THIS-MUST-NEVER-BE-PRINTED-0123456789";

        Directory.CreateDirectory(Path.Combine(box.Machine.Home, ".claude"));
        File.WriteAllText(Path.Combine(box.Machine.Home, ".claude", ".credentials.json"),
            $$"""{"token":"{{secret}}"}""");

        box.Put(Places.ClaudeCode, Level.Global, Purpose.Policy, """
            {"permissions":{"allow":["Bash(rm:*)","Read"]}}
            """);

        Diagnosis d = Examine(box);
        string everything = string.Join("\n",
            d.Clients.Select(c => c.Detail + c.Where).Concat(d.Findings.Select(f => f.Message + f.Where)));

        Assert.DoesNotContain(secret, everything);
        Assert.DoesNotContain("sk-", everything);
    }

    // ---- 6.8: containment holds for the closed list ----

    /// <summary>
    /// The doctor reads files no tree contains, which is the one
    /// exception to R8. It is kept honest by being a fixed list rather
    /// than a permission: never a caller-supplied path, never a glob,
    /// never a directory walk.
    /// </summary>
    [Fact]
    public void OnlyTheCompiledInListMayBeReadOrWritten()
    {
        using var box = new Fixture();

        Assert.True(Places.Allows(box.Machine, box.PathOf(Places.ClaudeCode, Level.Global, Purpose.Policy)));
        Assert.True(Places.Allows(box.Machine, box.PathOf(Places.ClaudeCode, Level.Global, Purpose.Policy)
            + Places.BackupSuffix));

        foreach (string forbidden in new[]
        {
            Path.Combine(box.Machine.Home, ".claude", ".credentials.json"),
            Path.Combine(box.Machine.Home, ".ssh", "id_rsa"),
            Path.Combine(box.Machine.Tree, "anything.json"),
            Path.Combine(box.Machine.Home, ".claude", "settings.json.other"),
        })
            Assert.False(Places.Allows(box.Machine, forbidden), $"{forbidden} must not be readable");
    }

    // ---- setup ----

    static Result<Scaffolding> Setup(Fixture box, Scaffold.Options options) =>
        Scaffold.Apply(box.Machine, options, "test");

    /// <summary>
    /// 6.4, and the whole risk of the feature. These files hold other
    /// people's servers. A merge bug here does visible damage to a
    /// machine outside this repository.
    /// </summary>
    [Fact]
    public void MergingKeepsEveryOtherServerExactlyAsItWas()
    {
        using var box = new Fixture();
        box.Put(Places.ClaudeCode, Level.Repo, Purpose.Servers, """
            {
              "mcpServers": {
                "one":   { "command": "one-binary",   "args": ["a", "b"] },
                "two":   { "command": "two-binary",   "env": { "K": "V" } },
                "three": { "command": "three-binary" }
              },
              "somethingElse": { "kept": true }
            }
            """);

        Result<Scaffolding> done = Setup(box, new Scaffold.Options(Places.ClaudeCode, Level.Repo));
        Assert.True(done.IsOk, done.Failure?.Message);

        JsonElement after = JsonDocument.Parse(
            File.ReadAllText(box.PathOf(Places.ClaudeCode, Level.Repo, Purpose.Servers))).RootElement;

        JsonElement servers = after.GetProperty("mcpServers");
        Assert.Equal("one-binary", servers.GetProperty("one").GetProperty("command").GetString());
        Assert.Equal(["a", "b"], servers.GetProperty("one").GetProperty("args")
            .EnumerateArray().Select(a => a.GetString()));
        Assert.Equal("V", servers.GetProperty("two").GetProperty("env").GetProperty("K").GetString());
        Assert.Equal("three-binary", servers.GetProperty("three").GetProperty("command").GetString());
        Assert.True(after.GetProperty("somethingElse").GetProperty("kept").GetBoolean());

        Assert.Equal("serve", servers.GetProperty("fettler").GetProperty("args")[0].GetString());
    }

    /// <summary>3.3: back up before the first write, beside the original,
    /// and say where it went.</summary>
    [Fact]
    public void TheFirstWriteLeavesABackupAndSaysWhereItIs()
    {
        using var box = new Fixture();
        string path = box.Put(Places.ClaudeCode, Level.Repo, Purpose.Servers, """{"mcpServers":{}}""");

        Result<Scaffolding> done = Setup(box, new Scaffold.Options(Places.ClaudeCode, Level.Repo));

        Assert.True(done.IsOk, done.Failure?.Message);
        Assert.Contains(done.Value.Changes, c => c.Backup == path + Places.BackupSuffix);
        Assert.True(File.Exists(path + Places.BackupSuffix));
        Assert.Equal("""{"mcpServers":{}}""", File.ReadAllText(path + Places.BackupSuffix));
    }

    /// <summary>6.5: running it twice changes nothing the second time -
    /// for JSON and for the marker blocks alike.</summary>
    [Fact]
    public void SetupIsIdempotent()
    {
        using var box = new Fixture();

        Assert.True(Setup(box, new Scaffold.Options(Places.ClaudeCode, Level.Repo)).IsOk);

        string servers = File.ReadAllText(box.PathOf(Places.ClaudeCode, Level.Repo, Purpose.Servers));
        string instructions = File.ReadAllText(box.PathOf(Places.ClaudeCode, Level.Repo, Purpose.Instructions));
        string policy = File.ReadAllText(box.PathOf(Places.ClaudeCode, Level.Repo, Purpose.Policy));

        Result<Scaffolding> again = Setup(box, new Scaffold.Options(Places.ClaudeCode, Level.Repo));

        Assert.True(again.IsOk, again.Failure?.Message);
        Assert.Empty(again.Value.Changes);
        Assert.Equal(servers, File.ReadAllText(box.PathOf(Places.ClaudeCode, Level.Repo, Purpose.Servers)));
        Assert.Equal(instructions, File.ReadAllText(box.PathOf(Places.ClaudeCode, Level.Repo, Purpose.Instructions)));
        Assert.Equal(policy, File.ReadAllText(box.PathOf(Places.ClaudeCode, Level.Repo, Purpose.Policy)));
    }

    /// <summary>An unmarked instruction block always ends up appended
    /// twice; a marked one updates in place.</summary>
    [Fact]
    public void TheInstructionBlockUpdatesInPlaceAndKeepsWhatWasAlreadyWritten()
    {
        using var box = new Fixture();
        box.Put(Places.ClaudeCode, Level.Repo, Purpose.Instructions,
            "# House rules\n\nSomething the user wrote themselves.\n");

        Assert.True(Setup(box, new Scaffold.Options(Places.ClaudeCode, Level.Repo)).IsOk);
        Assert.True(Setup(box, new Scaffold.Options(Places.ClaudeCode, Level.Repo)).IsOk);

        string text = File.ReadAllText(box.PathOf(Places.ClaudeCode, Level.Repo, Purpose.Instructions));

        Assert.Contains("Something the user wrote themselves.", text);
        Assert.Equal(1, text.Split(Scaffold.BlockOpen).Length - 1);
    }

    /// <summary>--local writes the deny list by default: it lands in the
    /// project's own settings, is reviewable in a diff, and affects only
    /// this tree.</summary>
    [Fact]
    public void SetupLocalWritesTheDenyList()
    {
        using var box = new Fixture();

        Assert.True(Setup(box, new Scaffold.Options(Places.ClaudeCode, Level.Repo)).IsOk);

        JsonElement deny = JsonDocument.Parse(
            File.ReadAllText(box.PathOf(Places.ClaudeCode, Level.Repo, Purpose.Policy)))
            .RootElement.GetProperty("permissions").GetProperty("deny");

        string[] denied = [.. deny.EnumerateArray().Select(d => d.GetString()!)];
        foreach (string tool in Doctor.Denied) Assert.Contains(tool, denied);
    }

    /// <summary>--global only OFFERS: changing how the assistant may work
    /// in every project is not a side effect of configuring one.</summary>
    [Fact]
    public void SetupGlobalOnlyOffersTheDenyListUntilItIsAskedFor()
    {
        using var box = new Fixture();

        Result<Scaffolding> offered = Setup(box, new Scaffold.Options(Places.ClaudeCode, Level.Global));
        Assert.True(offered.IsOk, offered.Failure?.Message);
        Assert.Contains(offered.Value.Notes, n => n.Contains("not written"));
        Assert.False(File.Exists(box.PathOf(Places.ClaudeCode, Level.Global, Purpose.Policy)));

        Result<Scaffolding> asked = Setup(box,
            new Scaffold.Options(Places.ClaudeCode, Level.Global, Deny: true));
        Assert.True(asked.IsOk, asked.Failure?.Message);
        Assert.Contains("Read", File.ReadAllText(box.PathOf(Places.ClaudeCode, Level.Global, Purpose.Policy)));
    }

    /// <summary>An existing allow entry is the user's own configuration.
    /// A tool that quietly removes it has done exactly what this design
    /// objects to.</summary>
    [Fact]
    public void ExistingAllowEntriesAreNamedAndNeverDeleted()
    {
        using var box = new Fixture();
        box.Put(Places.ClaudeCode, Level.Repo, Purpose.Policy, """
            {"permissions":{"allow":["Read(//c/x/**)","Bash(git status)"]}}
            """);

        Result<Scaffolding> done = Setup(box, new Scaffold.Options(Places.ClaudeCode, Level.Repo));

        Assert.True(done.IsOk, done.Failure?.Message);
        Assert.Contains(done.Value.Notes, n => n.Contains("LEFT ALONE") && n.Contains("Read(//c/x/**)"));
        Assert.Contains("Read(//c/x/**)",
            File.ReadAllText(box.PathOf(Places.ClaudeCode, Level.Repo, Purpose.Policy)));
    }

    [Fact]
    public void ADryRunTouchesNothingAtAllAndSaysWhatItWould()
    {
        using var box = new Fixture();

        Result<Scaffolding> done = Setup(box, new Scaffold.Options(Places.ClaudeCode, Level.Repo, DryRun: true));

        Assert.True(done.IsOk, done.Failure?.Message);
        Assert.NotEmpty(done.Value.Changes);
        Assert.All(done.Value.Changes, c => Assert.False(c.Applied));
        Assert.False(File.Exists(box.PathOf(Places.ClaudeCode, Level.Repo, Purpose.Servers)));
        Assert.False(File.Exists(Path.Combine(box.Machine.Tree, RootsFile.FileName)));
    }

    /// <summary>3.x: re-running never silently reshapes somebody's own
    /// registration.</summary>
    [Fact]
    public void ARegistrationThatSaysSomethingElseNeedsForceToBeReplaced()
    {
        using var box = new Fixture();
        box.Put(Places.ClaudeCode, Level.Repo, Purpose.Servers, """
            {"mcpServers":{"fettler":{"command":"somebody-elses-choice","args":["--special"]}}}
            """);

        Result<Scaffolding> refused = Setup(box, new Scaffold.Options(Places.ClaudeCode, Level.Repo));
        Assert.False(refused.IsOk);
        Assert.Equal(Outcome.TargetExists, refused.Failure!.Outcome);
        Assert.Contains("somebody-elses-choice",
            File.ReadAllText(box.PathOf(Places.ClaudeCode, Level.Repo, Purpose.Servers)));

        Result<Scaffolding> forced = Setup(box, new Scaffold.Options(Places.ClaudeCode, Level.Repo, Force: true));
        Assert.True(forced.IsOk, forced.Failure?.Message);
        Assert.DoesNotContain("somebody-elses-choice",
            File.ReadAllText(box.PathOf(Places.ClaudeCode, Level.Repo, Purpose.Servers)));
    }

    /// <summary>6.6: a file that will not parse is one setup must not
    /// overwrite, because overwriting throws away whatever it holds.</summary>
    [Fact]
    public void SetupRefusesToOverwriteAFileItCannotRead()
    {
        using var box = new Fixture();
        box.Put(Places.ClaudeCode, Level.Repo, Purpose.Servers, "{ half a file");

        Result<Scaffolding> done = Setup(box, new Scaffold.Options(Places.ClaudeCode, Level.Repo));

        Assert.False(done.IsOk);
        Assert.Equal(Outcome.Invalid, done.Failure!.Outcome);
        Assert.Equal("{ half a file", File.ReadAllText(box.PathOf(Places.ClaudeCode, Level.Repo, Purpose.Servers)));
    }

    [Fact]
    public void SetupWritesATreeConfigurationWhereThereIsNone()
    {
        using var box = new Fixture();

        Assert.True(Setup(box, new Scaffold.Options(Places.ClaudeCode, Level.Repo)).IsOk);

        Result<RootsFile.Found> read = RootsFile.Read(Path.Combine(box.Machine.Tree, RootsFile.FileName));
        Assert.True(read.IsOk, read.Failure?.Message);
        Assert.Equal("work", read.Value.Trees[0].Name);
        Assert.Equal(Permissions.Full, read.Value.Trees[0].Can);
    }

    /// <summary>
    /// B.14: creating a configuration where none exists is scaffolding;
    /// REWRITING one is the escape B.13 closes, and belongs to a person
    /// with an editor.
    /// </summary>
    [Fact]
    public void SetupNeverChangesATreeConfigurationThatIsAlreadyThere()
    {
        using var box = new Fixture();
        string path = Path.Combine(box.Machine.Tree, RootsFile.FileName);
        const string mine = """{"trees":{"mine":{"path":".","can":["list","read"]}}}""";
        File.WriteAllText(path, mine);

        Assert.True(Setup(box, new Scaffold.Options(Places.ClaudeCode, Level.Repo)).IsOk);

        Assert.Equal(mine, File.ReadAllText(path));
    }

    // ---- 13: the hook ----

    /// <summary>
    /// There is a working SessionStart hook on the machine this was
    /// written for, running an unrelated script. Replacing that array
    /// would silently remove somebody's working setup.
    /// </summary>
    [Fact]
    public void TheHookIsAppendedToWhateverIsAlreadyThere()
    {
        using var box = new Fixture();
        box.Put(Places.ClaudeCode, Level.Repo, Purpose.Policy, """
            {"hooks":{"SessionStart":[{"hooks":[{"type":"command","command":"somebody-elses-script.ps1"}]}]}}
            """);

        Assert.True(Setup(box, new Scaffold.Options(Places.ClaudeCode, Level.Repo, Hooks: true)).IsOk);

        string text = File.ReadAllText(box.PathOf(Places.ClaudeCode, Level.Repo, Purpose.Policy));
        JsonElement sessions = JsonDocument.Parse(text).RootElement
            .GetProperty("hooks").GetProperty("SessionStart");

        Assert.Equal(2, sessions.GetArrayLength());
        Assert.Contains("somebody-elses-script.ps1", text);
        Assert.Contains("doctor --hook", text);
    }

    [Fact]
    public void TheHookIsNotInstalledTwice()
    {
        using var box = new Fixture();

        Assert.True(Setup(box, new Scaffold.Options(Places.ClaudeCode, Level.Repo, Hooks: true)).IsOk);
        Assert.True(Setup(box, new Scaffold.Options(Places.ClaudeCode, Level.Repo, Hooks: true)).IsOk);

        JsonElement sessions = JsonDocument.Parse(
            File.ReadAllText(box.PathOf(Places.ClaudeCode, Level.Repo, Purpose.Policy)))
            .RootElement.GetProperty("hooks").GetProperty("SessionStart");

        Assert.Equal(1, sessions.GetArrayLength());
    }

    [Fact]
    public void TheHookIsBoundedByATimeout()
    {
        using var box = new Fixture();
        Assert.True(Setup(box, new Scaffold.Options(Places.ClaudeCode, Level.Repo, Hooks: true)).IsOk);

        Assert.Contains("\"timeout\"",
            File.ReadAllText(box.PathOf(Places.ClaudeCode, Level.Repo, Purpose.Policy)));
    }

    /// <summary>
    /// A dry run must describe the one interesting case rather than
    /// refuse it. It first did the opposite - refused, and advised the
    /// very flag the caller had already passed - which was found by
    /// running the documented commands exactly as written.
    /// </summary>
    [Fact]
    public void ADryRunDescribesAReplacementInsteadOfRefusingIt()
    {
        using var box = new Fixture();
        box.Put(Places.ClaudeCode, Level.Repo, Purpose.Servers, """
            {"mcpServers":{"fettler":{"command":"something-else"}}}
            """);

        Result<Scaffolding> done = Setup(box, new Scaffold.Options(Places.ClaudeCode, Level.Repo, DryRun: true));

        Assert.True(done.IsOk, done.Failure?.Message);
        Assert.Contains(done.Value.Changes, c => c.What.Contains("REPLACE") && c.What.Contains("--force"));
        Assert.All(done.Value.Changes, c => Assert.False(c.Applied));
        Assert.Contains("something-else",
            File.ReadAllText(box.PathOf(Places.ClaudeCode, Level.Repo, Purpose.Servers)));
    }

    [Fact]
    public void AnUnknownClientIsRefusedNamingTheOnesThereAre()
    {
        using var box = new Fixture();

        Result<Scaffolding> done = Setup(box, new Scaffold.Options("emacs", Level.Repo));

        Assert.False(done.IsOk);
        Assert.Contains("claude-code", done.Failure!.Message);
    }
}
