// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

namespace Shoddy.Mcp;

/// <summary>
/// Where the server's files live, and who is allowed to create it.
///
/// ONE ROOT PER PROCESS, shared by every session. Per-session roots are
/// not offered and shall not be added: `SCRIBBLERSAVE` hands its path
/// straight to Png.Write, so what actually contains it is the PROCESS
/// WORKING DIRECTORY, and a process has one of those. A per-session root
/// would be a promise the scribbler save path could not keep.
///
/// THE ASYMMETRY IN THE TWO CASES IS DELIBERATE. A directory Sparky owns
/// is Sparky's to create; a directory the user named is one they meant,
/// so a typo must be reported rather than materialised somewhere they
/// will never think to look.
/// </summary>
public static class SparkyRoot
{
    /// <summary>`--root PATH` if given — used as given, and a startup
    /// failure if it does not exist. Otherwise LocalApplicationData/
    /// Shoddy/Sparky, created if absent.
    ///
    /// LocalApplicationData rather than Roaming: this directory holds
    /// generated artefacts — tapes, saved words, PNGs — alongside its
    /// config, and roaming would sync chart images around an enterprise
    /// profile. `--root` is the only override; there is no environment
    /// variable, because one way to say a thing is enough.</summary>
    public static string Resolve(string[] args)
    {
        string? named = Named(args);
        if (named != null)
        {
            if (!Directory.Exists(named))
                throw new DirectoryNotFoundException(
                    $"--root '{named}' does not exist. A granted `file` capability needs a " +
                    "real directory behind it, and a root you named is one you meant — so " +
                    "this is reported rather than created.");
            return Path.GetFullPath(named);
        }

        string own = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Shoddy", "Sparky");
        Directory.CreateDirectory(own);
        return own;
    }

    /// <summary>`--root PATH` or `--root=PATH`. A `--root` with nothing
    /// after it is a mistake worth naming rather than a silent fall back
    /// to the default, which would put the session's files somewhere the
    /// caller did not ask for.</summary>
    static string? Named(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--root=", StringComparison.Ordinal))
                return args[i]["--root=".Length..];
            if (args[i] != "--root") continue;
            if (i + 1 >= args.Length)
                throw new ArgumentException("--root needs a directory after it");
            return args[i + 1];
        }
        return null;
    }
}
