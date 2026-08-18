using System.Text;
using Fettler.Cli;
using Fettler.Core;
using Fettler.Mcp;

namespace Fettle;

/// <summary>
/// The whole executable: read argv, hand it to one of the two front ends
/// in Fettler, write what comes back, return the exit code. Every
/// decision worth making was made in Fettler and is tested there without
/// a process.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] argv)
    {
        // A BOM on stdout corrupts the first JSON-RPC message of a
        // session and produces a parse error the client blames on the
        // server. It is also noise in front of a script's answer.
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        using var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            // Second Ctrl-C gets the default behaviour: a server that
            // will not stop when asked twice deserves to be killed.
            e.Cancel = !stopping.IsCancellationRequested;
            if (!stopping.IsCancellationRequested) stopping.Cancel();
        };

        // R2.7: the front end is chosen by an explicit subcommand, not by
        // the name the executable was invoked under. Selection by argv[0]
        // is the Unix convention and rests on a mechanism not in ordinary
        // use on Windows; a subcommand is unambiguous on both.
        if (argv.Length > 0 && argv[0].Equals("serve", StringComparison.OrdinalIgnoreCase))
            return await Serve(argv, stopping.Token).ConfigureAwait(false);

        CliResult result = await Command
            .RunAsync(argv, Console.In, stopping.Token).ConfigureAwait(false);

        // R3.7: the complete result, failures included, is already in
        // Stdout when --json was asked for. Stderr carries the human
        // diagnostic and nothing a script is expected to parse.
        if (result.Stdout.Length > 0) Console.Out.Write(result.Stdout);
        if (result.Stderr.Length > 0) Console.Error.Write(result.Stderr);

        await Console.Out.FlushAsync().ConfigureAwait(false);
        return result.ExitCode;
    }

    static async Task<int> Serve(string[] argv, CancellationToken cancel)
    {
        Arguments args = Arguments.Parse(argv);

        Result<Boundary> boundary = args.DeclaredBoundary();
        if (!boundary.IsOk)
        {
            // R8.5: a root the caller named that does not exist is a
            // startup failure naming the path, not a directory Fettler
            // quietly creates. This one goes to stderr because there is
            // no protocol session yet to carry it.
            Console.Error.WriteLine($"fettle serve: {boundary.Failure!.Message}"
                + (boundary.Failure.Path is null ? "" : $" ({boundary.Failure.Path})"));
            return ExitCodes.Of(boundary.Failure.Outcome);
        }

        // A boundary read from a file is re-read from that same file
        // before every request, so an edit binds without a restart;
        // --root declares one with no file behind it, and that one
        // stays as launched.
        Func<Result<Roots>>? reload = boundary.Value.File is { } file
            ? () => RootsFile.Reopen(file)
            : null;

        using var bench = new Bench(boundary.Value.Roots);
        using var server = new McpServer(bench, Console.In, Console.Out, reload);

        await server.RunAsync(cancel).ConfigureAwait(false);
        return ExitCodes.Ok;
    }
}
