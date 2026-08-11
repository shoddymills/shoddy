// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Text;
using Shoddy.Mcp;

// Sparky: a local MCP server over the reckoner dictionary.
//
//     sparky [--root PATH]
//
// Named after Karen Spärck Jones (1935-2007), who gave information
// retrieval inverse document frequency and spent a career arguing that
// computing was too important to be left to men. The name is apt twice
// over: her subject was retrieving the right material for a question,
// and half this server's job is retrieving the right grounding for a
// word.
//
// STDIN AND STDOUT ARE THE PROTOCOL. Nothing else may write to stdout —
// the engine's PRINT goes to each session's own writer, and every
// diagnostic here goes to stderr. A stray line on stdout is a parse
// error at the client.

try
{
    // Containment is ShoddyHostOptions.FileRoot's, and it is a real
    // boundary: every file word resolves against the root and is
    // refused outside it. The chdir is not what enforces that and is
    // not needed for it — it is here so that anything OUTSIDE the
    // engine which resolves a relative path (a crash dump, a trace
    // file) lands in the same place as everything else rather than in
    // whatever directory the client happened to launch us from.
    string root = SparkyRoot.Resolve(args);
    Directory.SetCurrentDirectory(root);

    // Stdout without a byte-order mark: a BOM ahead of the first message
    // is a parse error at the client, and it is invisible in a log.
    var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
    {
        AutoFlush = false,      // the server flushes once per message
    };
    var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));

    Console.Error.WriteLine($"sparky: root {root}");

    // `net` is granted and armed for this process's Mode N engines only
    // — ShoddyHost.Load sets and restores the ambient switch inside a
    // construction gate. ArmNetForProcess is a Mode T concern and is
    // never called. The grant is outbound-only by construction rather
    // than by policy: seednet bridges NETGET and NETREQUEST, and the
    // server half is not in the dictionary at all.
    using var tools = new SparkyTools(root, allowNet: true);
    var server = new SparkyServer(tools, stdin, stdout);
    await server.RunAsync();
    return 0;
}
catch (DirectoryNotFoundException e)
{
    // A root the user named and that does not exist is a startup
    // failure, never a directory to materialise beside the executable:
    // a granted `file` capability with no real directory behind it must
    // fail here, where it can be read, rather than later somewhere
    // nobody will look.
    Console.Error.WriteLine("sparky: " + e.Message);
    return 2;
}
catch (ArgumentException e)
{
    Console.Error.WriteLine("sparky: " + e.Message);
    Console.Error.WriteLine("usage: sparky [--root PATH]");
    return 2;
}
