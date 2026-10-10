// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Text.Json;
using Shoddy.Mcp;
using Shoddy.Runtime;
using Xunit;

namespace Shoddy.Mcp.Tests;

/// <summary>
/// The server, with no client, no window, no display and no network.
///
/// The scribbler registry is process-global static state, so these run in
/// one collection rather than concurrently: a canvas installed by one
/// fixture and cleared by another would make a third fixture's drawing
/// silently headless.
/// </summary>
[CollectionDefinition("sparky")]
public class SparkyCollection { }

[Collection("sparky")]
public class FoldTests
{
    /// <summary>
    /// The fold, from the two manifests, in BOTH directions — which is
    /// the test that keeps the exclusion true a year from now rather
    /// than only today.
    ///
    /// A seed that joins halifax and not sparky fails here unless it is
    /// one of the three named exclusions; a seed on the exclusion list
    /// that finds its way back into sparky's closure fails here too. It
    /// reads the shipped manifests rather than a list in this file for
    /// the usual reason: a copied detail is a detail that goes stale.
    /// </summary>
    [Fact]
    public void SparkysFoldIsHalifaxsLessTheThreeExclusions()
    {
        string[] excluded = { "seedbuzzer", "seedkeys", "seedvt100" };

        HashSet<string> halifax = Seeds("halifax");
        HashSet<string> sparky = Seeds("sparky");

        Assert.True(halifax.Count > 30, "halifax's manifest looks unbuilt: " + halifax.Count);

        string[] missing = halifax.Except(sparky).Except(excluded).OrderBy(s => s).ToArray();
        Assert.True(missing.Length == 0,
            "halifax folds these and sparky does not, and none is a named exclusion: "
          + string.Join(", ", missing));

        string[] returned = sparky.Intersect(excluded).OrderBy(s => s).ToArray();
        Assert.True(returned.Length == 0,
            "these are on the exclusion list and are back in sparky's fold: "
          + string.Join(", ", returned));

        string[] extra = sparky.Except(halifax).OrderBy(s => s).ToArray();
        Assert.True(extra.Length == 0,
            "sparky folds seeds halifax does not, which is not what its fold is: "
          + string.Join(", ", extra));
    }

    /// <summary>
    /// The grounding prompt on docs/mills/sparky.html is the text the
    /// server serves, and not a paraphrase of it.
    ///
    /// The page reproduces it for the case with no server to ask — a
    /// system prompt, a project instruction file, a model briefed before
    /// it connects — and a reproduction that drifts is worse than none:
    /// it would ground a model in rules the engine no longer keeps. So
    /// the two are compared exactly, against the page as a reader sees
    /// it and the Copy button copies it: entities decoded, so `&amp;mdash;`
    /// in the source matches the served `—`.
    /// </summary>
    [Fact]
    public void TheDocsPageCarriesTheServedGroundingWordForWord()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "docs")))
            dir = dir.Parent;
        string page = System.Net.WebUtility.HtmlDecode(File.ReadAllText(
            Path.Combine(dir!.FullName, "docs", "mills", "sparky.html"))).Replace("\r\n", "\n");

        Assert.Contains(SparkyGrounding.Reckoner.Replace("\r\n", "\n"), page);
        Assert.Contains(SparkyGrounding.Surface.Replace("\r\n", "\n"), page);
    }

    /// <summary>The four the engine cannot refuse. Everything else a
    /// caller gets wrong earns a refusal that names the fix, so the
    /// briefing can be short about those — these are the ones that
    /// return a plausible number instead, and the prompt is the only
    /// thing standing between a model and a confident wrong answer.</summary>
    [Theory]
    [InlineData("radians")]        // 90 SIN is 0.894, not 1
    [InlineData("STDDEVP")]        // sample and population are different words
    [InlineData("FIX")]            // changes what is shown, not what is held
    [InlineData("MONEY")]          // doubles do not do currency
    public void TheGroundingWarnsAboutWhatCannotBeRefused(string fragment)
    {
        Assert.Contains("cannot catch you", SparkyGrounding.Reckoner);
        Assert.Contains(fragment, SparkyGrounding.Reckoner);
    }

    static HashSet<string> Seeds(string mill)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "mills")))
            dir = dir.Parent;
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(dir!.FullName, "mills", mill, "mill.manifest")));
        return manifest.RootElement.GetProperty("machines").EnumerateArray()
            .Select(m => m.GetString()!)
            .Where(m => m.StartsWith("seed", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}

[Collection("sparky")]
public class ServerTests : IDisposable
{
    readonly string root;
    readonly SparkyTools tools;
    readonly SparkyServer server;

    public ServerTests()
    {
        root = Path.Combine(Path.GetTempPath(), "shoddy-sparky", Guid.NewGuid().ToString("N"), "root");
        Directory.CreateDirectory(root);
        tools = new SparkyTools(root, allowNet: false);
        server = new SparkyServer(tools, TextReader.Null, TextWriter.Null);
    }

    public void Dispose() => tools.Dispose();

    // ---- the protocol ----

    [Fact]
    public async Task InitializeAnswersTheVersionTheClientAskedForWhenItKnowsIt()
    {
        JsonElement r = await Ask("""
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05"}}
            """);
        Assert.Equal("2024-11-05", r.GetProperty("result").GetProperty("protocolVersion").GetString());
        Assert.Equal("sparky", r.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
    }

    [Fact]
    public async Task AVersionItDoesNotKnowGetsTheNewestItHas()
    {
        JsonElement r = await Ask("""
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"1999-01-01"}}
            """);
        Assert.Equal("2025-06-18", r.GetProperty("result").GetProperty("protocolVersion").GetString());
    }

    /// <summary>A notification has no id, and the protocol forbids
    /// answering one. A server that replied would put an unsolicited
    /// message on a client's stream.</summary>
    [Fact]
    public async Task ANotificationEarnsNoAnswerAtAll()
    {
        Assert.Null(await server.AnswerAsync("""
            {"jsonrpc":"2.0","method":"notifications/initialized"}
            """));
    }

    [Fact]
    public async Task AMalformedMessageIsAProtocolError()
    {
        JsonElement r = await Ask("not json at all");
        Assert.Equal(-32700, r.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task EveryToolIsListedWithASchema()
    {
        JsonElement r = await Ask("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        JsonElement[] listed = r.GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();
        string[] names = listed.Select(t => t.GetProperty("name").GetString()!).ToArray();
        foreach (string want in new[] { "eval", "define", "put", "stack", "help", "view", "words",
                                        "canvas", "save", "load", "tape", "machines",
                                        "subject", "reset", "abandon" })
            Assert.Contains(want, names);
        foreach (JsonElement t in listed)
        {
            Assert.False(string.IsNullOrWhiteSpace(t.GetProperty("description").GetString()));
            Assert.Equal(JsonValueKind.Object, t.GetProperty("inputSchema").ValueKind);
        }
    }

    // ---- a turn keeps its three parts apart ----

    [Fact]
    public async Task PrintedSaidAndStackAreNeverMerged()
    {
        string said = await Eval("1 [ DUP PRINT 2 * ] 3 TIMES");
        Assert.Contains("[printed] 1", said);
        Assert.Contains("[printed] 2", said);
        Assert.Contains("[printed] 4", said);
        Assert.Contains("[stack] x: 8", said);
        // What was printed is not what the line answered, and the loop's
        // passes must not appear as stack rows.
        Assert.DoesNotContain("[stack] x: 1", said);
    }

    /// <summary>A refused line is a RESULT. isError is for a malformed
    /// request; a refusal is the engine working correctly, and a client
    /// told otherwise will retry rather than read.</summary>
    [Fact]
    public async Task ARefusedLineIsAResultAndTheStackSurvives()
    {
        JsonElement r = await Call("eval", """{"lines":["3 4 +","1 0 /","3 4 +"]}""");
        Assert.False(r.GetProperty("result").GetProperty("isError").GetBoolean());
        string said = Text(r);
        Assert.Contains("[refused]", said);
        Assert.Contains("cannot divide by zero", said);
        // The refusal left the stack exactly as the line found it, and
        // the line after it landed on top of what was already there —
        // so the session went on with both sevens.
        Assert.Contains("[stack] y: 7", said);
        Assert.EndsWith("[stack] x: 7", said);
    }

    [Fact]
    public async Task AMalformedRequestIsTheOneThingThatIsAnError()
    {
        JsonElement r = await Call("help", "{}");
        Assert.True(r.GetProperty("result").GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task NothingPrintedLeaksFromOneTurnIntoTheNext()
    {
        await Eval("\"first turn\" PRINT");
        string second = await Eval("3 4 +");
        Assert.DoesNotContain("first turn", second);
    }

    [Fact]
    public async Task TwoSessionsDoNotInterfere()
    {
        await Call("eval", """{"lines":["111"],"session":"a"}""");
        await Call("eval", """{"lines":["222"],"session":"b"}""");
        // The `stack` tool answers the render itself, not a turn, so the
        // rows arrive without eval's field prefixes.
        Assert.Contains("x: 111", Text(await Call("stack", """{"session":"a"}""")));
        Assert.Contains("x: 222", Text(await Call("stack", """{"session":"b"}""")));
    }

    // ---- grounding is read, never authored ----

    [Fact]
    public async Task HelpComesFromTheRunningDictionary()
    {
        string help = Text(await Call("help", """{"word":"MEAN"}"""));
        Assert.Contains("MEAN", help);
        Assert.Contains("(", help);                 // its stack effect
        Assert.Contains("touches nothing", help);
    }

    [Fact]
    public async Task AWordOfAnExcludedSeedIsNotInTheDictionary()
    {
        foreach (string word in new[] { "BUZZPLAY", "KEYNAME", "VTEVALKEY", "QUIT" })
            Assert.Contains("is not a word in this dictionary",
                Text(await Call("help", "{\"word\":\"" + word + "\"}")));
    }

    [Fact]
    public async Task NoListeningOrWaitingWordCanBeReached()
    {
        string words = Text(await Call("words", "{}")).ToUpperInvariant();
        foreach (string word in new[] { "LISTEN", "ACCEPT", "WAITACCEPT", "WAITRECV",
                                        "INPUT", "INPUTLINE", "INKEY" })
            Assert.DoesNotContain(" " + word + " ", " " + words.Replace("\n", " ") + " ");
    }

    [Fact]
    public async Task ASubjectCardCarriesProseAndTheLiveWordList()
    {
        string card = Text(await Call("subject", """{"machine":"seed-alg"}"""));
        Assert.Contains("ALGSIMPLIFY", card);                   // live, from the dictionary
        Assert.Contains("running dictionary", card);
        Assert.True(card.Length > 400, "the card carries no prose: " + card);
    }

    [Fact]
    public async Task TheGroundingResourcesReadBack()
    {
        foreach (string uri in new[] { "sparky://grounding/shape", "sparky://grounding/reckoner",
                                       "sparky://grounding/surface", "sparky://dictionary" })
        {
            JsonElement r = await Ask(
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"resources/read\","
                + "\"params\":{\"uri\":\"" + uri + "\"}}");
            string text = r.GetProperty("result").GetProperty("contents")[0].GetProperty("text").GetString()!;
            Assert.True(text.Length > 200, uri + " came back nearly empty");
        }
        // The two rules a Forth-shaped guess gets wrong must be in there.
        JsonElement rk = await Ask("""
            {"jsonrpc":"2.0","id":1,"method":"resources/read","params":{"uri":"sparky://grounding/reckoner"}}
            """);
        string reckoner = rk.GetProperty("result").GetProperty("contents")[0].GetProperty("text").GetString()!;
        Assert.Contains("takes exactly one cell", reckoner);
        Assert.Contains("A word cannot call itself", reckoner);
    }

    // ---- isolation ----

    [Fact]
    public async Task APathCannotClimbOutOfTheRoot()
    {
        string outside = Path.Combine(Path.GetDirectoryName(root)!, "escaped.tape");
        await Eval("\"../escaped.tape\" { \"out\" } WRITELINES");
        Assert.False(File.Exists(outside), "a file word climbed out of the root");

        await Eval("\"kept.tape\" { \"in\" } WRITELINES");
        Assert.True(File.Exists(Path.Combine(root, "kept.tape")), "a plain name did not land in the root");
    }

    [Fact]
    public async Task NetIsRefusedWhenItWasNotGranted()
    {
        // This fixture withheld it, so the gated builtins answer that
        // rather than reaching a socket.
        Assert.Contains("[stack] x: False", await Eval("NETALLOWED"));
    }

    // ---- what one answer may carry ----

    /// <summary>A STRING cell used to render whole, and the whole stack
    /// is rendered after every line, so a long string left at y: came
    /// back with every later answer. cuttle abbreviates it now; this is
    /// the server showing the abbreviated row with the value intact.</summary>
    [Fact]
    public async Task ALongStringRendersBoundedOnTheStack()
    {
        string first = await Eval("1 2000 RANGE [ STR ] MAP \",\" JOIN");
        string row = first.Split('\n').Last(l => l.StartsWith("[stack] x: "));
        Assert.True(row.Length <= "[stack] x: ".Length + 76, "the row ran past the bound: " + row.Length);
        Assert.Contains("... 8833 more", row);
        Assert.Contains("[stack] x: 8892", await Eval("DUP LEN"));
    }

    [Fact]
    public async Task PrintedOutputIsCappedPerTurnAndTheRestCounted()
    {
        string many = await Eval("1 1000 RANGE [ DUP PRINT ] MAP");
        Assert.Contains("[printed] 200\n", many);
        Assert.DoesNotContain("[printed] 201\n", many);
        Assert.Contains("[printed] ... 800 more lines not shown", many);

        string few = await Eval("1 199 RANGE [ DUP PRINT ] MAP");
        Assert.Contains("[printed] 199\n", few);
        Assert.DoesNotContain("not shown", few);

        string one = await Eval("1 5000 RANGE [ STR ] MAP \",\" JOIN PRINT");
        string line = one.Split('\n').First(l => l.StartsWith("[printed] 1,2,3"));
        Assert.Equal("[printed] ".Length + SparkyTools.PrintedCharCap, line.Length);
        Assert.Contains("[printed] ... 1 more line not shown", one);
        Assert.DoesNotContain("not shown", await Eval("3 4 +"));
    }

    /// <summary>The tokenizer has no string escapes, so a JSON document
    /// cannot be typed into a line. put is the door: the text goes in
    /// whole, and every answer that follows stays small.</summary>
    [Fact]
    public async Task PutBanksTextPastTheTokenizerAndTheAnswersStaySmall()
    {
        var periods = new System.Text.StringBuilder();
        for (int i = 1; i <= 400; i++)
        {
            if (i > 1) periods.Append(',');
            periods.Append("{\"name\":\"Period ").Append(i).Append("\",\"temperature\":").Append(60 + i % 20).Append('}');
        }
        string json = "{\"properties\":{\"periods\":[" + periods + "]}}";
        Assert.True(json.Length > 12_000, "the fixture is too small to prove anything: " + json.Length);

        string banked = Text(await Call("put", JsonSerializer.Serialize(new { name = "doc", text = json })));
        Assert.Equal("banked " + json.Length + " characters as doc", banked);

        string parsed = await Eval("\"doc\" RCL JSONPARSE \"doc\" STO");
        Assert.DoesNotContain("[refused]", parsed);
        string walked = await Eval("\"doc\" RCL { \"properties\" \"periods\" 1 \"name\" } DPATH");
        Assert.Contains("[stack] x: \"Period 1\"", walked);
        foreach (string answer in new[] { banked, parsed, walked })
            Assert.True(answer.Length < 2000, "an answer carried the document: " + answer.Length);

        string tape = Text(await Call("tape", "{}"));
        Assert.Contains("put doc: " + json.Length + " characters", tape);
        Assert.DoesNotContain("\"periods\":[", tape);
    }

    [Fact]
    public async Task PutWithoutANameIsAMalformedRequest()
    {
        JsonElement r = await Call("put", """{"text":"x"}""");
        Assert.True(r.GetProperty("result").GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task ShapeDescribesAValueWithoutRenderingIt()
    {
        await Eval("1 25 RANGE [ DUP STR SWAP PAIR ] MAP");
        string shape = await Eval("SHAPE");
        Assert.Contains("[said] DICT of 25 entries", shape);
        Assert.Contains("[said]   \"1\": NUMBER 1", shape);
        Assert.Contains("[said]   ... 5 more", shape);
        Assert.DoesNotContain("[said]   \"21\"", shape);
        Assert.EndsWith("more }", shape.TrimEnd());   // the stack row is still the dict, abbreviated
    }

    // ---- the harness ----

    async Task<string> Eval(string line) =>
        Text(await Call("eval", JsonSerializer.Serialize(new { lines = new[] { line } })));

    async Task<JsonElement> Call(string tool, string args) => await Ask(
        "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\""
        + tool + "\",\"arguments\":" + args + "}}");

    async Task<JsonElement> Ask(string message)
    {
        string? reply = await server.AnswerAsync(message);
        Assert.NotNull(reply);
        return JsonDocument.Parse(reply!).RootElement;
    }

    static string Text(JsonElement reply) => string.Join("\n",
        reply.GetProperty("result").GetProperty("content").EnumerateArray()
             .Where(b => b.GetProperty("type").GetString() == "text")
             .Select(b => b.GetProperty("text").GetString()));
}

/// <summary>
/// The canvas: a drawing must be able to reach a client that has no
/// screen, by both routes and after the surface is shut.
/// </summary>
[Collection("sparky")]
public class CanvasTests : IDisposable
{
    readonly string root;
    readonly SparkyTools tools;
    readonly SparkyServer server;

    public CanvasTests()
    {
        root = Path.Combine(Path.GetTempPath(), "shoddy-sparky-draw", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        tools = new SparkyTools(root, allowNet: false);
        server = new SparkyServer(tools, TextReader.Null, TextWriter.Null);
    }

    public void Dispose()
    {
        tools.Dispose();
        ScribblerRegistry.OpenCount = 0;
    }

    [Fact]
    public async Task ABlittedChartComesBackInTheSameAnswer()
    {
        JsonElement r = await Eval(
            "640 480 \"p\" PLOTOPEN", "\"p\" { 1 2 2 3 3 3 } 4 PLOTHISTOGRAM", "\"p\" PLOTBLIT");
        Assert.True(Png(r).Length > 100, "the blitted chart did not come back as a picture");
    }

    [Fact]
    public async Task ADrawingNobodyBlittedIsStillThereToAskFor()
    {
        await Eval("320 200 \"t\" TURTLEOPEN", "\"t\" 100 TURTLEFORWARD");
        JsonElement r = await Call("canvas", "{}");
        Assert.True(Png(r).Length > 100, "a drawn-but-unblitted surface answered nothing");
    }

    /// <summary>The capture table is the HOST's, not the runtime's, so
    /// it outlives the shut: a student who closes before asking can
    /// still be shown what they made.</summary>
    [Fact]
    public async Task AClosedSurfaceKeepsItsLastFrame()
    {
        await Eval("640 480 \"p\" PLOTOPEN", "\"p\" { 1 2 3 } 3 PLOTHISTOGRAM", "\"p\" PLOTBLIT");
        await Eval("\"p\" CLOSE");
        Assert.True(Png(await Call("canvas", "{}")).Length > 100,
            "the frame went with the surface");
    }

    [Fact]
    public async Task AskingBeforeAnythingIsDrawnSaysSoRatherThanFailing()
    {
        JsonElement r = await Call("canvas", "{}");
        Assert.False(r.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Contains("nothing has been drawn", Text(r));
    }

    async Task<JsonElement> Eval(params string[] lines) =>
        await Call("eval", JsonSerializer.Serialize(new { lines }));

    async Task<JsonElement> Call(string tool, string args)
    {
        string? reply = await server.AnswerAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\""
            + tool + "\",\"arguments\":" + args + "}}");
        Assert.NotNull(reply);
        return JsonDocument.Parse(reply!).RootElement;
    }

    static byte[] Png(JsonElement reply)
    {
        JsonElement? image = reply.GetProperty("result").GetProperty("content").EnumerateArray()
            .Cast<JsonElement?>()
            .FirstOrDefault(b => b!.Value.GetProperty("type").GetString() == "image");
        Assert.True(image is not null, "no picture in the answer: " + Text(reply));
        return Convert.FromBase64String(image!.Value.GetProperty("data").GetString()!);
    }

    static string Text(JsonElement reply) => string.Join("\n",
        reply.GetProperty("result").GetProperty("content").EnumerateArray()
             .Where(b => b.GetProperty("type").GetString() == "text")
             .Select(b => b.GetProperty("text").GetString()));
}
