// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using Shoddy.Runtime;

namespace Shoddy.Hosting;

/// <summary>
/// The paths a HOST names itself under its file root: the rc file it
/// loads on the way in, and the files its save, load and tape words
/// write and read.
///
/// The engine's own file words are contained by Shoddy.Runtime.FileRoot
/// on every call. These paths never reach the engine, and before this
/// both hosts resolved them with a plain Path.Combine, which hands an
/// absolute path back unchanged and lets `..` climb out of the root.
/// Both hosts resolve them here now, by the rule the engine applies.
/// </summary>
public static class HostPath
{
    /// <summary>The full path to use, or null when the path lies
    /// outside the root. With no root, the path is handed back as it
    /// is, which is what the engine does with no root too.</summary>
    public static string? Under(string? root, string path)
    {
        string? real = FileRoot.Of(root);
        return real is null ? path : FileRoot.Contain(real, path);
    }
}
