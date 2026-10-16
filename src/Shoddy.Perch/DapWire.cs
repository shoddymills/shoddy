// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shoddy.Perch;

/// <summary>The protocol's framing, written once for everything here
/// that speaks it: a Content-Length header, a blank line, then the JSON
/// body. The length counts bytes, not characters.</summary>
public static class DapWire
{
    static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The next message's body, or null at the end of the
    /// stream.</summary>
    public static byte[]? Read(Stream input)
    {
        int len = -1;
        var line = new StringBuilder();
        while (true)                    // headers
        {
            int c = input.ReadByte();
            if (c < 0) return null;
            if (c == '\n')
            {
                string h = line.ToString().TrimEnd('\r');
                line.Clear();
                if (h.Length == 0) break;
                if (h.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    len = int.Parse(h["Content-Length:".Length..].Trim());
            }
            else line.Append((char)c);
        }
        if (len < 0) return null;
        var buf = new byte[len];
        int got = 0;
        while (got < len)
        {
            int n = input.Read(buf, got, len - got);
            if (n <= 0) return null;
            got += n;
        }
        return buf;
    }

    /// <summary>A message's body: the payload as JSON, with nothing
    /// written for a field left null, as the protocol's optional
    /// fields are left out rather than nulled.</summary>
    public static byte[] Body(object payload) =>
        JsonSerializer.SerializeToUtf8Bytes(payload, Options);

    /// <summary>One message, framed and flushed. A writer shared between
    /// threads serializes its calls here.</summary>
    public static void Write(Stream output, byte[] body)
    {
        byte[] head = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        output.Write(head);
        output.Write(body);
        output.Flush();
    }
}
