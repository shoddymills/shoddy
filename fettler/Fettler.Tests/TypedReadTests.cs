using System.Text.Json;
using Fettler.Cli;
using Fettler.Core;
using Fettler.Mcp;
using Xunit;

namespace Fettler.Tests;

/// <summary>
/// 9b: the file types a developer actually has, and the line cap that
/// makes routing every read through this tool affordable.
///
/// <para>These exist because <c>Read</c> is denied to the assistant
/// outright, which makes Fettler the only reader. Every one of these
/// tests is a type somebody would otherwise simply be unable to open.</para>
/// </summary>
public sealed class TypedReadTests
{
    static Task<CliResult> Run(Sandbox box, params string[] argv) =>
        Command.RunAsync(argv, new StringReader(string.Empty), box.Bench);

    // ---- 9b.1: notebooks ----

    const string Notebook = """
        {
          "cells": [
            { "cell_type": "markdown", "source": ["# Title\n", "Some prose.\n"] },
            {
              "cell_type": "code",
              "source": "print('hello')\n",
              "outputs": [
                { "output_type": "stream", "name": "stdout", "text": ["hello\n"] },
                { "output_type": "display_data",
                  "data": { "image/png": "AAAABBBBCCCCDDDD", "text/plain": ["<Figure>"] } }
              ]
            },
            {
              "cell_type": "code",
              "source": "boom()",
              "outputs": [ { "output_type": "error", "ename": "NameError", "evalue": "boom is not defined" } ]
            }
          ],
          "metadata": {},
          "nbformat": 4
        }
        """;

    [Fact]
    public async Task ANotebookIsReadAsCellsRatherThanAsTheJsonItIsStoredIn()
    {
        using var box = new Sandbox();
        box.Write("analysis.ipynb", Notebook);

        CliResult read = await Run(box, "read", "analysis.ipynb");

        Assert.Equal(ExitCodes.Ok, read.ExitCode);
        Assert.Contains("cell 1 markdown", read.Stdout);
        Assert.Contains("# Title", read.Stdout);
        Assert.Contains("print('hello')", read.Stdout);
        Assert.Contains("NameError: boom is not defined", read.Stdout);

        // The JSON scaffolding it is stored in is not what a reader wants.
        Assert.DoesNotContain("nbformat", read.Stdout);
        Assert.DoesNotContain("cell_type", read.Stdout);
    }

    /// <summary>
    /// 9b.9. A single output cell routinely holds a megabyte of base64
    /// PNG, and pasting one into a conversation costs more context than
    /// the whole notebook's code for a picture nobody can see.
    /// </summary>
    [Fact]
    public async Task ANotebooksNonTextOutputIsMeasuredRatherThanPasted()
    {
        using var box = new Sandbox();
        box.Write("analysis.ipynb", Notebook);

        CliResult read = await Run(box, "read", "analysis.ipynb");

        Assert.DoesNotContain("AAAABBBBCCCCDDDD", read.Stdout);
        Assert.Contains("image/png", read.Stdout);
        Assert.Contains("not shown", read.Stdout);
    }

    [Fact]
    public async Task ALongStreamOutputIsCappedAndSaysHowMuchWasLeft()
    {
        using var box = new Sandbox();
        string lines = string.Join("", Enumerable.Range(0, 200).Select(i => $"\"row {i}\\n\","));
        box.Write("loud.ipynb", $$"""
            { "cells": [ { "cell_type": "code", "source": "x",
              "outputs": [ { "output_type": "stream", "text": [{{lines.TrimEnd(',')}}] } ] } ] }
            """);

        CliResult read = await Run(box, "read", "loud.ipynb");

        Assert.Contains("row 0", read.Stdout);
        Assert.DoesNotContain("row 199", read.Stdout);
        Assert.Contains("more lines of output not shown", read.Stdout);
    }

    [Fact]
    public async Task AFileNamedAsANotebookThatIsNotOneIsRefusedSayingSo()
    {
        using var box = new Sandbox();
        box.Write("lying.ipynb", "this is not a notebook at all");

        CliResult read = await Run(box, "read", "lying.ipynb");

        Assert.NotEqual(ExitCodes.Ok, read.ExitCode);
        Assert.Contains("notebook", read.Stderr);
    }

    // ---- 9b.2: images ----

    /// <summary>A one-pixel PNG, written byte by byte so the test states
    /// exactly what it means rather than depending on a library.</summary>
    static byte[] OnePixelPng() =>
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x07,                             // width 7
        0x00, 0x00, 0x00, 0x03,                             // height 3
        0x08, 0x06, 0x00, 0x00, 0x00,
        0x1F, 0x15, 0xC4, 0x89,
    ];

    [Fact]
    public async Task AnImageAnswersItsFactsAndTheTerminalIsNotSentBase64()
    {
        using var box = new Sandbox();
        box.WriteRaw("shot.png", OnePixelPng());

        CliResult human = await Run(box, "read", "shot.png");

        Assert.Equal(ExitCodes.Ok, human.ExitCode);
        Assert.Contains("image/png", human.Stdout);
        Assert.Contains("7x3", human.Stdout);
        Assert.DoesNotContain("iVBOR", human.Stdout);
    }

    [Fact]
    public async Task TheMachineReadableAnswerCarriesTheBytes()
    {
        using var box = new Sandbox();
        box.WriteRaw("shot.png", OnePixelPng());

        CliResult json = await Run(box, "read", "shot.png", "--json");
        JsonElement file = JsonDocument.Parse(json.Stdout).RootElement.GetProperty("files")[0];

        Assert.Equal("image", file.GetProperty("kind").GetString());
        Assert.Equal("image/png", file.GetProperty("mime_type").GetString());
        Assert.Equal(Convert.ToBase64String(OnePixelPng()), file.GetProperty("base64").GetString());
    }

    /// <summary>
    /// The MCP content model carries an image natively, so the bytes go
    /// there rather than sitting in a text field as unreadable base64 -
    /// and they are not in both places, which would cost the caller the
    /// same context twice.
    /// </summary>
    [Fact]
    public async Task OverMcpAnImageBecomesAnImagePartAndLeavesTheText()
    {
        using var box = new Sandbox();
        box.WriteRaw("shot.png", OnePixelPng());

        using var server = new McpServer(box.Bench, TextReader.Null, TextWriter.Null);
        string? reply = await server.AnswerAsync("""
            {"jsonrpc":"2.0","id":1,"method":"tools/call",
             "params":{"name":"read","arguments":{"paths":["shot.png"]}}}
            """);

        JsonElement content = JsonDocument.Parse(reply!).RootElement
            .GetProperty("result").GetProperty("content");

        Assert.Equal(2, content.GetArrayLength());
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.DoesNotContain("base64", content[0].GetProperty("text").GetString());
        Assert.Contains("image/png", content[0].GetProperty("text").GetString());

        Assert.Equal("image", content[1].GetProperty("type").GetString());
        Assert.Equal("image/png", content[1].GetProperty("mimeType").GetString());
        Assert.Equal(Convert.ToBase64String(OnePixelPng()), content[1].GetProperty("data").GetString());
    }

    [Fact]
    public async Task AnImageWhoseBytesDisagreeWithItsNameIsRefused()
    {
        using var box = new Sandbox();
        box.Write("lying.png", "plainly text");

        CliResult read = await Run(box, "read", "lying.png");

        Assert.NotEqual(ExitCodes.Ok, read.ExitCode);
        Assert.Contains("do not agree", read.Stderr);
    }

    // ---- 9b.3: PDF ----

    static byte[] APdf(string body)
    {
        var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        UglyToad.PdfPig.Writer.PdfDocumentBuilder.AddedFont font =
            builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);

        UglyToad.PdfPig.Writer.PdfPageBuilder page = builder.AddPage(595, 842);
        page.AddText(body, 12, new UglyToad.PdfPig.Core.PdfPoint(40, 800), font);

        return builder.Build();
    }

    [Fact]
    public async Task APdfIsReadAsTextPageByPage()
    {
        using var box = new Sandbox();
        box.WriteRaw("requirements.pdf", APdf("The requirement is written here"));

        CliResult read = await Run(box, "read", "requirements.pdf");

        Assert.Equal(ExitCodes.Ok, read.ExitCode);
        Assert.Contains("page 1 of 1", read.Stdout);
        Assert.Contains("Therequirementiswrittenhere".Substring(0, 3), read.Stdout);
    }

    [Fact]
    public async Task ThePdfHashIsOfTheFileAndNotOfTheRendering()
    {
        using var box = new Sandbox();
        byte[] bytes = APdf("anything");
        box.WriteRaw("doc.pdf", bytes);

        CliResult json = await Run(box, "read", "doc.pdf", "--json");
        JsonElement file = JsonDocument.Parse(json.Stdout).RootElement.GetProperty("files")[0];

        Assert.Equal("pdf", file.GetProperty("kind").GetString());
        Assert.Equal(TextIo.HashOf(bytes), file.GetProperty("hash").GetString());
        Assert.Equal(bytes.Length, file.GetProperty("bytes").GetInt64());
    }

    [Fact]
    public async Task AFileNamedAsAPdfThatIsNotOneIsRefusedRatherThanCrashing()
    {
        using var box = new Sandbox();
        box.Write("lying.pdf", "not a pdf, not even slightly");

        CliResult read = await Run(box, "read", "lying.pdf");

        Assert.NotEqual(ExitCodes.Ok, read.ExitCode);
        Assert.Contains("could not be read", read.Stderr);
    }

    // ---- 9b.8: the line cap ----

    /// <summary>
    /// find stops at a thousand and search at two hundred; read had no
    /// limit at all. Denying the assistant's own reader - which truncates
    /// at two thousand - and routing everything through an uncapped read
    /// would have made the context budget WORSE.
    /// </summary>
    [Fact]
    public async Task ReadStopsAtTheCapAndSaysHowToSeeTheRest()
    {
        using var box = new Sandbox();
        box.Write("long.txt", string.Join("", Enumerable.Range(1, 2500).Select(i => $"line {i}\n")));

        CliResult json = await Run(box, "read", "long.txt", "--json");
        JsonElement file = JsonDocument.Parse(json.Stdout).RootElement.GetProperty("files")[0];

        Assert.Equal(Bench.DefaultLineCap, file.GetProperty("to").GetInt32());
        Assert.Equal(2500, file.GetProperty("lines").GetInt32());
        Assert.True(file.GetProperty("truncated").GetBoolean());
        Assert.Equal(500, file.GetProperty("lines_remaining").GetInt32());

        CliResult human = await Run(box, "read", "long.txt");
        Assert.Contains("500 more line(s)", human.Stdout);
        Assert.Contains("--from 2001", human.Stdout);
    }

    [Fact]
    public async Task AnExplicitRangeLiftsTheCapBecauseTheCallerKnowsWhatTheyAsked()
    {
        using var box = new Sandbox();
        box.Write("long.txt", string.Join("", Enumerable.Range(1, 2500).Select(i => $"line {i}\n")));

        CliResult json = await Run(box, "read", "long.txt", "--from", "1", "--to", "2500", "--json");
        JsonElement file = JsonDocument.Parse(json.Stdout).RootElement.GetProperty("files")[0];

        Assert.Equal(2500, file.GetProperty("to").GetInt32());
        Assert.False(file.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task AFileShorterThanTheCapIsNotReportedAsTruncated()
    {
        using var box = new Sandbox();
        box.Write("short.txt", "one\ntwo\n");

        CliResult json = await Run(box, "read", "short.txt", "--json");
        JsonElement file = JsonDocument.Parse(json.Stdout).RootElement.GetProperty("files")[0];

        Assert.False(file.GetProperty("truncated").GetBoolean());
        Assert.Equal(2, file.GetProperty("to").GetInt32());
    }

    // ---- 9b.4: R4.5 becomes typed ----

    [Fact]
    public async Task AnUnrecognisedBinaryIsStillRefusedAsBinary()
    {
        using var box = new Sandbox();
        box.WriteRaw("mystery.bin", [0x00, 0x01, 0x02, 0x00, 0xFF]);

        CliResult read = await Run(box, "read", "mystery.bin");

        Assert.NotEqual(ExitCodes.Ok, read.ExitCode);
        Assert.Contains("binary", read.Stderr);
    }

    [Fact]
    public async Task CsvIsTextAndNeedsNothingSpecialAtAll()
    {
        using var box = new Sandbox();
        box.Write("figures.csv", "name,amount\nwool,42\n");

        CliResult read = await Run(box, "read", "figures.csv");

        Assert.Equal(ExitCodes.Ok, read.ExitCode);
        Assert.Contains("wool,42", read.Stdout);
    }
}
