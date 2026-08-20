using System.IO.Compression;
using System.Text;
using Fettler.Cli;
using Fettler.Core;
using Xunit;

namespace Fettler.Tests;

/// <summary>
/// The disclosure screen: what leaves a screened scope, and what does
/// not.
///
/// <para><b>Every fixture here is synthetic, and that is a requirement
/// rather than a convenience (R7.2).</b> The invented numbers are shaped
/// like the real thing - the card even passes Luhn, because otherwise it
/// would prove nothing - but no real person's data enters this tree, for
/// the same reason a refusal never quotes what it found.</para>
///
/// <para><b>No model runs in any of this.</b> The first tier needs none,
/// and the second is reached through <see cref="IScreener"/>, which a
/// fake implements - so the fail-closed behaviour is proven without 400
/// MB of weights. A gate that could only be exercised by installing
/// those is a gate nobody would exercise.</para>
/// </summary>
public sealed class ScreenTests
{
    // Invented, and shaped like the real thing so the detectors are
    // actually exercised.
    const string Ssn = "123-45-6789";
    const string Card = "4111 1111 1111 1111";        // passes Luhn
    const string BadCard = "4111 1111 1111 1112";     // does not
    const string Email = "renee@example.com";
    const string Phone = "(555) 123-4567";

    static Task<CliResult> Run(Sandbox box, params string[] argv) =>
        Command.RunAsync(argv, new StringReader(string.Empty), box.Bench);

    static Result<IReadOnlyList<ReadSlice>> ReadOf(Sandbox box, string path, int? from = null, int? to = null) =>
        box.Bench.Read(new ReadRequest([path], from, to));

    /// <summary>A stand-in for the model host, so the gate can be driven
    /// into every state the real one can reach.</summary>
    sealed class Fake(Func<string, Screened, Result<IReadOnlyList<ScreenFinding>>> answer) : IScreener
    {
        public string? Saw { get; private set; }

        public Screened Asked { get; private set; }

        public Result<IReadOnlyList<ScreenFinding>> Inspect(string payload, Screened categories)
        {
            Saw = payload;
            Asked = categories;
            return answer(payload, categories);
        }

        public static Fake Clean() =>
            new((_, _) => Result<IReadOnlyList<ScreenFinding>>.Ok([]));

        public static Fake Finds(Screened category, int many) =>
            new((_, _) =>
            {
                var found = new List<ScreenFinding>();
                for (int i = 0; i < many; i++) found.Add(new ScreenFinding(category, "model"));
                return Result<IReadOnlyList<ScreenFinding>>.Ok(found);
            });

        public static Fake Fails(string why) =>
            new((_, _) => Result<IReadOnlyList<ScreenFinding>>.Fail(Outcome.Screened, why));
    }

    // ================= the category grammar (R1.4, AC7) =================

    [Fact]
    public void ABareListIncludesOnlyWhatItNames() =>
        Assert.Equal(Screened.Phi | Screened.Legal, Screens.Parse(["phi", "legal"]).Value);

    [Fact]
    public void AMinusListExcludesFromEverything()
    {
        Result<Screened> parsed = Screens.Parse(["-sci"]);

        Assert.True(parsed.IsOk);
        Assert.Equal(Screens.Everything & ~Screened.Sci, parsed.Value);
        Assert.True(parsed.Value.HasFlag(Screened.Phi));
        Assert.False(parsed.Value.HasFlag(Screened.Sci));
    }

    /// <summary>
    /// AC7: mixing the two forms is refused, and the refusal names the
    /// ambiguity rather than picking one.
    ///
    /// <para><c>["phi", "-sci"]</c> reads either as "phi only" or as
    /// "everything but sci", and those differ by two whole categories. A
    /// rule choosing one would silently withhold a screen from somebody
    /// who believed they had asked for it.</para>
    /// </summary>
    [Fact]
    public void MixingIncludedAndExcludedIsRefused()
    {
        Result<Screened> parsed = Screens.Parse(["phi", "-sci"]);

        Assert.False(parsed.IsOk);
        Assert.Contains("cannot be combined", parsed.Failure!.Message, StringComparison.Ordinal);
    }

    /// <summary>A misspelt category is refused rather than ignored: it
    /// would otherwise quietly SUBTRACT a screen, and the failure would
    /// arrive as data leaving a tree everybody believed was covered.</summary>
    [Fact]
    public void AnUnknownCategoryIsRefusedAndNamed()
    {
        Result<Screened> parsed = Screens.Parse(["lgal"]);

        Assert.False(parsed.IsOk);
        Assert.Contains("lgal", parsed.Failure!.Message, StringComparison.Ordinal);
        Assert.Contains("legal", parsed.Failure!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ScreensAreWrittenBackInAFixedOrder()
    {
        Assert.Equal("phi pii legal sci", Screens.Write(Screens.Everything));
        Assert.Equal("nothing", Screens.Write(Screened.None));
    }

    // ---- read out of a configuration file ----

    static string Configured(string json)
    {
        string dir = Directory.CreateTempSubdirectory("fettle-screen-cfg-").FullName;
        File.WriteAllText(Path.Combine(dir, RootsFile.FileName), json);
        return Path.Combine(dir, RootsFile.FileName);
    }

    [Fact]
    public void ScreenTrueMeansEveryCategory()
    {
        Result<RootsFile.Found> found = RootsFile.Read(Configured(
            """{"trees":{"work":{"path":".","screen":true}}}"""));

        Assert.True(found.IsOk, found.Failure?.Message);
        Assert.Equal(Screens.Everything, found.Value.Trees[0].Screen);
    }

    [Fact]
    public void ATreeThatSaysNothingScreensNothing()
    {
        Result<RootsFile.Found> found = RootsFile.Read(Configured(
            """{"trees":{"work":{"path":"."}}}"""));

        Assert.True(found.IsOk, found.Failure?.Message);
        Assert.Equal(Screened.None, found.Value.Trees[0].Screen);
    }

    [Fact]
    public void AMixedListIsRefusedWhenTheConfigurationIsREAD()
    {
        Result<RootsFile.Found> found = RootsFile.Read(Configured(
            """{"trees":{"work":{"path":".","screen":["phi","-sci"]}}}"""));

        Assert.False(found.IsOk);
        Assert.Equal(Outcome.Invalid, found.Failure!.Outcome);
        Assert.Contains("cannot be combined", found.Failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AScreenThatIsNotTrueFalseOrAListIsRefused()
    {
        Result<RootsFile.Found> found = RootsFile.Read(Configured(
            """{"trees":{"work":{"path":".","screen":"phi"}}}"""));

        Assert.False(found.IsOk);
        Assert.Equal(Outcome.Invalid, found.Failure!.Outcome);
    }

    /// <summary>A scope's screen REPLACES the tree's, which is what lets
    /// a scope take screening away as well as tighten it.</summary>
    [Fact]
    public void AScopeReplacesTheTreesScreen()
    {
        Result<RootsFile.Found> found = RootsFile.Read(Configured(
            """
            {"trees":{"work":{"path":".","screen":true,
              "scopes":{"public":{"can":["list","read"],"screen":false}}}}}
            """));

        Assert.True(found.IsOk, found.Failure?.Message);
        Assert.Equal(Screened.None, found.Value.Trees[0].Scopes![0].Screen);
    }

    /// <summary>
    /// A scope that says nothing about screening INHERITS the tree's.
    ///
    /// <para>Reading silence as "screen nothing here" would punch a hole
    /// in the first tree anybody switched the screen on for, because
    /// every scope written before the feature existed says nothing.</para>
    /// </summary>
    [Fact]
    public void AScopeThatSaysNothingInheritsTheTrees()
    {
        Result<RootsFile.Found> found = RootsFile.Read(Configured(
            """
            {"trees":{"work":{"path":".","screen":["phi"],
              "scopes":{"inner":{"can":["list","read"]}}}}}
            """));

        Assert.True(found.IsOk, found.Failure?.Message);
        Assert.Null(found.Value.Trees[0].Scopes![0].Screen);

        var grant = new Grant(Permissions.Full,
            [new Scope("/t/inner", Permissions.ReadOnly)], Screened.Phi);

        Assert.Equal(Screened.Phi, grant.At("/t", "/t/inner/x").Screen);
    }

    [Fact]
    public void AScopeCanTakeScreeningAway()
    {
        var grant = new Grant(Permissions.Full,
            [new Scope("/t/open", Permissions.ReadOnly, Screened.None)], Screens.Everything);

        Assert.Equal(Screens.Everything, grant.At("/t", "/t/x").Screen);
        Assert.Equal(Screened.None, grant.At("/t", "/t/open/x").Screen);
    }

    // ================= tier one, with no sidecar at all =================

    /// <summary>AC6: tier one refuses a synthetic SSN with no sidecar
    /// installed. This is the whole of the screen on a machine where the
    /// models were never downloaded, and it still works.</summary>
    [Fact]
    public void TierOneRefusesAnSsnWithNoSidecar()
    {
        using var box = new Sandbox();
        box.Write("notes.txt", $"reference {Ssn} was filed\n");
        box.Screen(Screened.Pii);

        Result<IReadOnlyList<ReadSlice>> read = ReadOf(box, "notes.txt");

        Assert.False(read.IsOk);
        Assert.Equal(Outcome.Screened, read.Failure!.Outcome);
    }

    /// <summary>
    /// A pattern that runs out of time checked NOTHING, and a check that
    /// did not happen must never be reported as a check that found
    /// nothing.
    ///
    /// <para><b>Reached by lowering the ceiling rather than by finding a
    /// pathological payload.</b> Every detector is deliberately free of
    /// the nested quantifiers that make a pattern blow up, so no ordinary
    /// input gets near the one-second bound - which is a good property of
    /// the detectors and an awkward one for this test. One tick times out
    /// on anything, and it is the same code path either way.</para>
    ///
    /// <para><b><see cref="Searcher"/> answers the same exception the
    /// opposite way</b>, by skipping the line and carrying on, because
    /// there the cost of giving up is a missing search hit. Here it is a
    /// disclosure. The two look alike and must not be handled
    /// alike.</para>
    /// </summary>
    [Fact]
    public void APatternThatRunsOutOfTimeRefusesRatherThanServing()
    {
        Failure? refused = Disclosure.Check(
            $"reference {Ssn} was filed", Screened.Pii, null, null, TimeSpan.FromTicks(1));

        Assert.NotNull(refused);
        Assert.Equal(Outcome.Screened, refused.Outcome);
        Assert.Contains("ran out of time", refused.Message, StringComparison.Ordinal);

        // And the payload is one tier one WOULD have refused, so the
        // refusal cannot be mistaken for the ordinary detection: it says
        // the check did not happen, not that it found something.
        Assert.DoesNotContain("would disclose", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Ssn, refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The Luhn check is what makes the card detector worth having.
    ///
    /// <para>Without it the pattern is "sixteen digits in groups of
    /// four", which every document full of reference numbers matches -
    /// and a check that cries wolf is a check somebody switches off.</para>
    /// </summary>
    [Fact]
    public void ACardIsJudgedByLuhnAndNotByShape()
    {
        using var box = new Sandbox();
        box.Write("good.txt", $"card {Card}\n");
        box.Write("bad.txt", $"order {BadCard}\n");
        box.Screen(Screened.Pii);

        Assert.False(ReadOf(box, "good.txt").IsOk);
        Assert.True(ReadOf(box, "bad.txt").IsOk, "a number that fails Luhn is not a card");
    }

    [Theory]
    [InlineData("contact renee@example.com about it", Screened.Pii)]
    [InlineData("call (555) 123-4567 tomorrow", Screened.Pii)]
    [InlineData("MRN: A1234567", Screened.Phi)]
    [InlineData("DOB: 3/4/1980", Screened.Phi)]
    [InlineData("date of birth 12-01-1975", Screened.Phi)]
    public void EachStructuralDetectorRefuses(string text, Screened category)
    {
        using var box = new Sandbox();
        box.Write("f.txt", text + "\n");
        box.Screen(category);

        Result<IReadOnlyList<ReadSlice>> read = ReadOf(box, "f.txt");

        Assert.False(read.IsOk, $"'{text}' was served");
        Assert.Equal(Outcome.Screened, read.Failure!.Outcome);
    }

    /// <summary>
    /// The things that must NOT be refused.
    ///
    /// <para>A screen that denies ordinary numbers is one people turn
    /// off, and a screen that is off protects nothing. Nine bare digits
    /// are an order number far more often than a social security number,
    /// and nothing about them says which.</para>
    /// </summary>
    [Theory]
    [InlineData("order 123456789 shipped")]
    [InlineData("version 1.2.3 released")]
    [InlineData("the total was 1200 units")]
    [InlineData("commit 4111111111111111 in the log")]
    public void OrdinaryTextIsServed(string text)
    {
        using var box = new Sandbox();
        box.Write("f.txt", text + "\n");
        box.Screen(Screens.Everything);

        Assert.True(ReadOf(box, "f.txt").IsOk, $"'{text}' was refused");
    }

    /// <summary>A category that is not screened does not deny. The
    /// configuration says what is being looked for, and excluding one
    /// takes a single word.</summary>
    [Fact]
    public void ADetectorForAnUnscreenedCategoryDoesNotFire()
    {
        using var box = new Sandbox();
        box.Write("notes.txt", $"reference {Ssn}\n");
        box.Screen(Screened.Sci);

        Assert.True(ReadOf(box, "notes.txt").IsOk,
            "an SSN is pii, and this scope screens only sci");
    }

    [Fact]
    public void AnUnscreenedTreeIsUntouched()
    {
        using var box = new Sandbox();
        box.Write("notes.txt", $"{Ssn} {Card} {Email}\n");

        Assert.True(ReadOf(box, "notes.txt").IsOk);
    }

    // ================= payload semantics (AC1, AC2) =================

    const string ClinicalNote = """
        Ward round notes
        The patient was seen today and is comfortable.
        No concerns were raised on the round.
        MRN: A1234567
        DOB: 3/4/1980
        """;

    /// <summary>AC1: a read of a synthetic clinical note from a phi tree
    /// is refused; the refusal names phi and a count and quotes
    /// nothing.</summary>
    [Fact]
    public void AClinicalNoteIsRefusedNamingTheCategoryAndACount()
    {
        using var box = new Sandbox();
        box.Write("note.txt", ClinicalNote);
        box.Screen(Screened.Phi);

        Result<IReadOnlyList<ReadSlice>> read = ReadOf(box, "note.txt");

        Assert.False(read.IsOk);
        Assert.Equal(Outcome.Screened, read.Failure!.Outcome);
        Assert.Contains("phi", read.Failure.Message, StringComparison.Ordinal);
        Assert.Contains("2 in phi", read.Failure.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("A1234567", read.Failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("3/4/1980", read.Failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// AC2: the SAME file's PHI-free range is served.
    ///
    /// <para>This is the whole of R1.1 demonstrated. The object under
    /// judgement is the payload, not the file, so a document holding PHI
    /// on one line stays readable everywhere else - and the screen does
    /// not lock the very trees people most need help in.</para>
    /// </summary>
    [Fact]
    public void ThePhiFreeRangeOfTheSameFileIsServed()
    {
        using var box = new Sandbox();
        box.Write("note.txt", ClinicalNote);
        box.Screen(Screened.Phi);

        Result<IReadOnlyList<ReadSlice>> read = ReadOf(box, "note.txt", from: 1, to: 3);

        Assert.True(read.IsOk, read.Failure?.Message);
        Assert.Contains("comfortable", read.Value[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("A1234567", read.Value[0].Text, StringComparison.Ordinal);
    }

    // ================= search (AC3) =================

    [Fact]
    public void ACleanSearchOverAScreenedTreeIsServed()
    {
        using var box = new Sandbox();
        box.Write("clean.txt", "the quarterly total is fine\n");
        box.Write("notes.txt", $"reference {Ssn} was filed\n");
        box.Screen(Screened.Pii);

        Result<SearchAnswer> found = box.Bench.Search(new SearchRequest(["quarterly"]));

        Assert.True(found.IsOk, found.Failure?.Message);
        Assert.Single(found.Value.Hits);
    }

    /// <summary>AC3: the response whose hit records contain a detected
    /// entity is refused - the hit line is the disclosure, and it is the
    /// hit line that is judged.</summary>
    [Fact]
    public void ASearchWhoseHitsCarryAnEntityIsRefused()
    {
        using var box = new Sandbox();
        box.Write("clean.txt", "the quarterly total is fine\n");
        box.Write("notes.txt", $"reference {Ssn} was filed\n");
        box.Screen(Screened.Pii);

        Result<SearchAnswer> found = box.Bench.Search(new SearchRequest(["reference"]));

        Assert.False(found.IsOk);
        Assert.Equal(Outcome.Screened, found.Failure!.Outcome);
        Assert.DoesNotContain(Ssn, found.Failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The CONTEXT lines are part of the disclosure and are judged with
    /// the hit.
    ///
    /// <para>A hit whose own line is clean still discloses everything
    /// <c>--context</c> brings with it, and screening only the matched
    /// line would hand the neighbours over unread.</para>
    /// </summary>
    [Fact]
    public void ContextLinesAreScreenedWithTheHit()
    {
        using var box = new Sandbox();
        box.Write("notes.txt", $"the marker line\n{Ssn}\n");
        box.Screen(Screened.Pii);

        Assert.False(box.Bench.Search(new SearchRequest(["marker"], Context: 2)).IsOk);
        Assert.True(box.Bench.Search(new SearchRequest(["marker"])).IsOk,
            "with no context the hit line alone is clean and is served");
    }

    // ================= documents (AC4) =================

    static byte[] Package(params (string Part, string Xml)[] parts)
    {
        using var bytes = new MemoryStream();

        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
            foreach ((string part, string xml) in parts)
            {
                ZipArchiveEntry entry = zip.CreateEntry(part);
                using Stream stream = entry.Open();
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.Write(xml);
            }

        return bytes.ToArray();
    }

    const string ContentTypes = """
        <?xml version="1.0" encoding="UTF-8"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
          <Default Extension="xml" ContentType="application/xml"/>
        </Types>
        """;

    static byte[] AWorkbookHolding(string text) => Package(
        ("[Content_Types].xml", ContentTypes),
        ("xl/workbook.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"
                      xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheets><sheet name="Patients" sheetId="1" r:id="rId1"/></sheets>
            </workbook>
            """),
        ("xl/_rels/workbook.xml.rels", """
            <?xml version="1.0" encoding="UTF-8"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Target="worksheets/sheet1.xml"
                Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"/>
            </Relationships>
            """),
        ("xl/worksheets/sheet1.xml", $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <sheetData>
                <row r="1"><c r="A1" t="inlineStr"><is><t>{text}</t></is></c></row>
              </sheetData>
            </worksheet>
            """));

    static byte[] ADocumentHolding(string text) => Package(
        ("[Content_Types].xml", ContentTypes),
        ("word/document.xml", $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
              <w:body>
                <w:p><w:r><w:t>{text}</w:t></w:r></w:p>
              </w:body>
            </w:document>
            """));

    /// <summary>
    /// AC4: the same fixtures disclosed through extraction screen
    /// identically to their plain-text twins.
    ///
    /// <para>That equivalence is the point of putting the gate at the
    /// seam rather than in each reader: a workbook, a document and a text
    /// file are all just text leaving the tool, and nothing about the
    /// screen should know which produced it.</para>
    /// </summary>
    [Fact]
    public void AWorkbookAndADocumentScreenLikeTheirPlainTextTwins()
    {
        using var box = new Sandbox();
        Documents.Forget();

        box.Write("twin.txt", $"reference {Ssn} was filed\n");
        box.WriteRaw("book.xlsx", AWorkbookHolding($"reference {Ssn} was filed"));
        box.WriteRaw("terms.docx", ADocumentHolding($"reference {Ssn} was filed"));

        box.Screen(Screened.Pii);

        foreach (string path in new[] { "twin.txt", "book.xlsx", "terms.docx" })
        {
            Result<IReadOnlyList<ReadSlice>> read = ReadOf(box, path);

            Assert.False(read.IsOk, $"{path} was served");
            Assert.Equal(Outcome.Screened, read.Failure!.Outcome);
            Assert.DoesNotContain(Ssn, read.Failure.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AWorkbookWithNothingInItIsStillServed()
    {
        using var box = new Sandbox();
        Documents.Forget();

        box.WriteRaw("book.xlsx", AWorkbookHolding("the quarterly total is fine"));
        box.Screen(Screens.Everything);

        Assert.True(ReadOf(box, "book.xlsx").IsOk);
    }

    /// <summary>A screened scope does not hand over an image, because
    /// nothing here can read one - and a photographed discharge summary
    /// is exactly the disclosure the screen exists to stop.</summary>
    [Fact]
    public void AnImageIsRefusedFromAScreenedScope()
    {
        using var box = new Sandbox();

        var png = new byte[24];
        png[0] = 0x89; png[1] = 0x50; png[2] = 0x4E; png[3] = 0x47;
        png[19] = 1; png[23] = 1;
        box.WriteRaw("scan.png", png);

        box.Screen(Screened.Phi);

        Result<IReadOnlyList<ReadSlice>> read = ReadOf(box, "scan.png");

        Assert.False(read.IsOk);
        Assert.Equal(Outcome.Screened, read.Failure!.Outcome);
        Assert.Contains("image", read.Failure.Message, StringComparison.Ordinal);
    }

    // ================= the sidecar (AC5, R7.1) =================

    /// <summary>
    /// AC5: with burler absent, a screened disclosure is refused with a
    /// reason naming the sidecar.
    ///
    /// <para>The real <see cref="Sidecar"/> runs here, not a fake -
    /// "burler is not installed" is exactly the state a machine is in
    /// before anybody installs it, and it deserves to be tested as
    /// itself.</para>
    /// </summary>
    [Fact]
    public void WithBurlerAbsentAScreenedDisclosureIsRefusedNamingTheSidecar()
    {
        using var box = new Sandbox();
        box.Write("clean.txt", "the quarterly total is fine\n");

        // Naming a models directory is the act that makes the second tier
        // load-bearing: from here on, its absence is a refusal.
        box.Screen(Screened.Phi, models: box.Root);

        Result<IReadOnlyList<ReadSlice>> read = ReadOf(box, "clean.txt");

        Assert.False(read.IsOk, "tier one found nothing, but the second tier could not run");
        Assert.Equal(Outcome.Screened, read.Failure!.Outcome);
        Assert.Contains("burler", read.Failure.Message, StringComparison.Ordinal);
    }

    /// <summary>AC5, the other half: an unscreened tree is untouched. A
    /// missing sidecar is only a problem for a scope that asked to be
    /// screened.</summary>
    [Fact]
    public void WithBurlerAbsentAnUnscreenedTreeIsUntouched()
    {
        using var box = new Sandbox();
        box.Write("notes.txt", $"reference {Ssn}\n");
        box.Screen(Screened.None, models: box.Root);

        Assert.True(ReadOf(box, "notes.txt").IsOk);
    }

    /// <summary>
    /// With NO models directory declared, tier one is the whole screen
    /// and a clean payload is served.
    ///
    /// <para>Nobody has claimed a model is installed, so there is no
    /// second tier to have failed. This is what keeps the screen usable
    /// before anybody downloads 400 MB - and it is why naming a directory
    /// is a deliberate act with consequences.</para>
    /// </summary>
    [Fact]
    public void WithNoModelsDirectoryTierOneIsTheWholeScreen()
    {
        using var box = new Sandbox();
        box.Write("clean.txt", "the quarterly total is fine\n");
        box.Write("notes.txt", $"reference {Ssn}\n");
        box.Screen(Screens.Everything);

        Assert.True(ReadOf(box, "clean.txt").IsOk);
        Assert.False(ReadOf(box, "notes.txt").IsOk);
    }

    // ---- R7.1: the gate, driven by a fake ----

    [Fact]
    public void ADetectionByTheModelDeniesTheResponse()
    {
        using var box = new Sandbox();
        box.Write("clean.txt", "the quarterly total is fine\n");
        box.Screen(Screened.Phi, Fake.Finds(Screened.Phi, 3));

        Result<IReadOnlyList<ReadSlice>> read = ReadOf(box, "clean.txt");

        Assert.False(read.IsOk);
        Assert.Equal(Outcome.Screened, read.Failure!.Outcome);
        Assert.Contains("3 in phi", read.Failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("the screening sidecar closed the pipe without answering")]
    [InlineData("the screening sidecar did not answer within 30 seconds")]
    [InlineData("the screening sidecar answered something that is not JSON")]
    [InlineData("the screening sidecar refused: no model for 'phi'")]
    public void EveryWayTheSidecarCanFailIsARefusal(string why)
    {
        using var box = new Sandbox();
        box.Write("clean.txt", "the quarterly total is fine\n");
        box.Screen(Screened.Phi, Fake.Fails(why));

        Result<IReadOnlyList<ReadSlice>> read = ReadOf(box, "clean.txt");

        Assert.False(read.IsOk, "a screen that could not run must never serve");
        Assert.Equal(Outcome.Screened, read.Failure!.Outcome);
        Assert.Contains(why, read.Failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACleanVerdictFromTheModelServesTheResponse()
    {
        using var box = new Sandbox();
        box.Write("clean.txt", "the quarterly total is fine\n");
        box.Screen(Screened.Phi, Fake.Clean());

        Assert.True(ReadOf(box, "clean.txt").IsOk);
    }

    /// <summary>
    /// The model is handed the payload, and only the categories the scope
    /// screens.
    ///
    /// <para>Running a category the configuration excluded would burn the
    /// inference and tempt somebody to report the answer.</para>
    /// </summary>
    [Fact]
    public void TheModelSeesThePayloadAndOnlyTheScreenedCategories()
    {
        using var box = new Sandbox();
        box.Write("clean.txt", "the quarterly total is fine\n");

        Fake fake = Fake.Clean();
        box.Screen(Screened.Phi | Screened.Sci, fake);

        Assert.True(ReadOf(box, "clean.txt").IsOk);
        Assert.Equal(Screened.Phi | Screened.Sci, fake.Asked);
        Assert.Contains("quarterly", fake.Saw!, StringComparison.Ordinal);
    }

    /// <summary>Tier one runs FIRST, so a payload it refuses never costs
    /// an inference - and the screen still works when the second tier is
    /// the thing that is broken.</summary>
    [Fact]
    public void TierOneRefusesBeforeTheModelIsEverAsked()
    {
        using var box = new Sandbox();
        box.Write("notes.txt", $"reference {Ssn}\n");

        Fake fake = Fake.Fails("this should never be reached");
        box.Screen(Screened.Pii, fake);

        Result<IReadOnlyList<ReadSlice>> read = ReadOf(box, "notes.txt");

        Assert.False(read.IsOk);
        Assert.Null(fake.Saw);
        Assert.Contains("1 in pii", read.Failure!.Message, StringComparison.Ordinal);
    }

    // ================= the wire, this side of it (R3.3) =================

    [Fact]
    public void AnAnswerWithFindingsIsRead()
    {
        Result<IReadOnlyList<ScreenFinding>> read = Sidecar.Read(
            """{"ok":true,"findings":[{"category":"phi","count":2,"spans":[[3,9],[20,28]]}]}""");

        Assert.True(read.IsOk, read.Failure?.Message);
        Assert.Equal(2, read.Value.Count);
        Assert.All(read.Value, one => Assert.Equal(Screened.Phi, one.Category));
    }

    [Fact]
    public void AnEmptyFindingsArrayIsACleanVerdict()
    {
        Result<IReadOnlyList<ScreenFinding>> read = Sidecar.Read("""{"ok":true,"findings":[]}""");

        Assert.True(read.IsOk, read.Failure?.Message);
        Assert.Empty(read.Value);
    }

    /// <summary>
    /// A MISSING findings array is a fault, not a clean verdict.
    ///
    /// <para>"I checked and it is clean" and "I did not answer properly"
    /// must not be spelled the same way, or a broken sidecar reads as a
    /// tree with nothing in it.</para>
    /// </summary>
    [Theory]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("""{"findings":[]}""")]
    [InlineData("""{"ok":true}""")]
    [InlineData("""{"ok":true,"findings":{}}""")]
    [InlineData("""{"ok":true,"findings":[{"category":"phi"}]}""")]
    [InlineData("""{"ok":true,"findings":[{"count":1}]}""")]
    [InlineData("""{"ok":true,"findings":[{"category":"lgal","count":1}]}""")]
    public void AMalformedAnswerIsARefusalAndNeverAPass(string line)
    {
        Result<IReadOnlyList<ScreenFinding>> read = Sidecar.Read(line);

        Assert.False(read.IsOk, $"'{line}' was read as an answer");
        Assert.Equal(Outcome.Screened, read.Failure!.Outcome);
    }

    [Fact]
    public void ARefusalFromTheSidecarCarriesItsReason()
    {
        Result<IReadOnlyList<ScreenFinding>> read = Sidecar.Read(
            """{"ok":false,"error":"no model for 'phi'"}""");

        Assert.False(read.IsOk);
        Assert.Contains("no model for 'phi'", read.Failure!.Message, StringComparison.Ordinal);
    }

    // ================= the refusal itself (R5) =================

    /// <summary>
    /// R5.1: nothing the screen finds is ever repeated back.
    ///
    /// <para>A refusal naming the patient it found would write PHI into a
    /// log, a transcript and whatever ships those onward - the exact harm
    /// the screen exists to prevent, delivered by the screen itself.</para>
    /// </summary>
    [Theory]
    [InlineData(Ssn, Screened.Pii)]
    [InlineData(Card, Screened.Pii)]
    [InlineData(Email, Screened.Pii)]
    [InlineData(Phone, Screened.Pii)]
    [InlineData("A1234567", Screened.Phi)]
    public void TheRefusalNeverRepeatsWhatItFound(string secret, Screened category)
    {
        using var box = new Sandbox();
        box.Write("f.txt", category == Screened.Phi ? $"MRN: {secret}\n" : $"value {secret}\n");
        box.Screen(category);

        Result<IReadOnlyList<ReadSlice>> read = ReadOf(box, "f.txt");

        Assert.False(read.IsOk);
        Assert.DoesNotContain(secret, read.Failure!.Message, StringComparison.Ordinal);

        // Not even a fragment of it: the first run of digits on its own
        // would be enough to reconstruct most of these.
        Assert.DoesNotContain(secret.Split(' ', '-', '@')[0], read.Failure.Message,
            StringComparison.Ordinal);
    }

    /// <summary>Nothing found leaks through the CLI either, which is the
    /// path that actually reaches a terminal and a transcript.</summary>
    [Fact]
    public async Task NothingLeaksThroughTheCommandLine()
    {
        using var box = new Sandbox();
        box.Write("notes.txt", $"reference {Ssn} was filed\n");
        box.Screen(Screened.Pii);

        CliResult said = await Run(box, "read", "notes.txt");

        Assert.Equal(ExitCodes.Screened, said.ExitCode);
        Assert.DoesNotContain(Ssn, said.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(Ssn, said.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("123-45", said.Stdout + said.Stderr, StringComparison.Ordinal);
    }

    /// <summary>R5.2: its own outcome and its own exit code, so a script
    /// can tell a screened refusal from every other kind without parsing
    /// text.</summary>
    [Fact]
    public void TheOutcomeHasItsOwnExitCodeAndName()
    {
        Assert.Equal(13, ExitCodes.Of(Outcome.Screened));
        Assert.Equal("screened", ExitCodes.NameOf(Outcome.Screened));

        Assert.NotEqual(ExitCodes.Of(Outcome.Refused), ExitCodes.Of(Outcome.Screened));
        Assert.NotEqual(ExitCodes.Of(Outcome.Credential), ExitCodes.Of(Outcome.Screened));
        Assert.NotEqual(ExitCodes.Of(Outcome.Governed), ExitCodes.Of(Outcome.Screened));
    }

    /// <summary>
    /// A screened refusal is not a credential refusal.
    ///
    /// <para>They are mirror images - one is a write going in, the other
    /// a payload coming out - and a script branching on "a secret was
    /// involved" would take entirely the wrong action for each.</para>
    /// </summary>
    [Fact]
    public void ScreeningDoesNotDisturbTheCredentialNet()
    {
        Assert.NotEqual(Outcome.Credential, Outcome.Screened);
        Assert.Equal(12, (int)Outcome.Screened);
        Assert.Equal(11, (int)Outcome.Credential);
    }

    // ================= the honesty clause is not decoration =================

    /// <summary>
    /// The documented limit, asserted so nobody can quietly come to
    /// believe otherwise: this is a safety net and NOT a boundary.
    ///
    /// <para>Split the number across two lines and every rule here is
    /// defeated - by accident as easily as on purpose. The test exists to
    /// make that concrete rather than to defend it, because a caller who
    /// believes a screened tree CANNOT leak has been misled, and that
    /// belief is the actual hazard.</para>
    /// </summary>
    [Fact]
    public void TheScreenIsASafetyNetAndNotABoundary()
    {
        using var box = new Sandbox();
        box.Write("split.txt", "reference 123-45-\n6789 was filed\n");
        box.Screen(Screens.Everything);

        Assert.True(ReadOf(box, "split.txt").IsOk,
            "a number split across a line break is not found, which is what "
            + "'a safety net and not a boundary' means in practice");
    }
}
