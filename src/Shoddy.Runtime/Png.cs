// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Buffers.Binary;
using System.IO.Compression;

namespace Shoddy.Runtime;

/// <summary>A minimal PNG writer, for SCRIBBLERSAVE. Eight-bit truecolour
/// (colour type 2), no interlacing, every scanline filtered None.
///
/// It lives in the runtime rather than the mill on purpose: a scribbler's
/// pixel buffer belongs to the runtime and nothing here needs a window, so
/// saving works headless — under the test harness, and under a
/// <c>--no-window</c> run whose whole job is to write a file.
///
/// ALPHA IS DROPPED. The buffer is RGBA but every write goes through
/// SetPixelClamped, which sets alpha to 255; untouched pixels are 0,0,0,0
/// and the window presents them as opaque black because the blit does not
/// blend. Writing RGB is therefore what the window shows, whereas RGBA
/// would hand a viewer a transparent border the user never saw on screen.
///
/// Filter None throughout. The adaptive filters buy their keep on
/// photographs; a chart is flat colour in long horizontal runs, which is
/// the one shape deflate already handles well, and None keeps this file
/// short enough to read in one sitting.</summary>
public static class Png
{
    static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>Write width x height pixels of RGBA (row-major, top-down,
    /// four bytes per pixel) to path as a PNG. Throws on any I/O failure —
    /// the caller turns that into a Shoddy error.</summary>
    public static void Write(string path, byte[] rgba, int width, int height)
    {
        if (width < 1 || height < 1)
            throw new ArgumentException($"PNG: size must be at least 1x1, got {width}x{height}");
        if (rgba.Length < width * height * 4)
            throw new ArgumentException("PNG: pixel buffer is shorter than its stated size");

        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        file.Write(Signature);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr[..4], width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.Slice(4, 4), height);
        ihdr[8] = 8;        // bit depth
        ihdr[9] = 2;        // colour type: truecolour, no alpha
        ihdr[10] = 0;       // compression: deflate, the only one there is
        ihdr[11] = 0;       // filter method: the only one there is
        ihdr[12] = 0;       // interlace: none
        Chunk(file, "IHDR", ihdr);

        Chunk(file, "IDAT", Deflate(rgba, width, height));
        Chunk(file, "IEND", ReadOnlySpan<byte>.Empty);
    }

    /// <summary>The raw scanlines — one filter byte then width RGB triples
    /// per row — run through zlib, which is what a PNG data chunk holds.
    /// Built in memory: a chart is a few hundred kilobytes before
    /// compression, and the chunk needs its length up front anyway.</summary>
    static byte[] Deflate(byte[] rgba, int width, int height)
    {
        var raw = new byte[height * (1 + width * 3)];
        int o = 0;
        for (int y = 0; y < height; y++)
        {
            raw[o++] = 0;                       // filter: None
            int i = y * width * 4;
            for (int x = 0; x < width; x++, i += 4)
            {
                raw[o++] = rgba[i];
                raw[o++] = rgba[i + 1];
                raw[o++] = rgba[i + 2];
            }
        }
        var buf = new MemoryStream();
        using (var z = new ZLibStream(buf, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(raw, 0, raw.Length);
        return buf.ToArray();
    }

    /// <summary>One chunk: big-endian length, four-byte type, the data, and
    /// a CRC-32 over the type AND the data (not the length — a PNG reader
    /// that includes it produces a file nothing else can read).</summary>
    static void Chunk(Stream to, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        to.Write(len);

        Span<byte> body = new byte[4 + data.Length];
        for (int k = 0; k < 4; k++) body[k] = (byte)type[k];
        data.CopyTo(body[4..]);
        to.Write(body);

        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(body));
        to.Write(crc);
    }

    // ---- CRC-32 (the PNG/zlib polynomial, reflected) --------------------
    // Hand-rolled rather than System.IO.Hashing.Crc32, which is a separate
    // NuGet package: fifteen lines is not worth a dependency in a runtime
    // whose whole point is that it carries almost none.

    static readonly uint[] Table = BuildTable();

    static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte b in data) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
