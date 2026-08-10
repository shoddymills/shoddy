// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using Shoddy.Runtime;

namespace Shoddy.Maui;

/// <summary>
/// The buzzer, complete (B3.7): install routes the shared channel
/// engine — which owns all seven delegates and every observable
/// behaviour — onto this platform's sink. One sink per platform, each
/// a hundred-odd lines of "play this PCM here", each from the platform
/// SDK MAUI already binds: no third-party audio package (D18).
///
/// A platform whose lane is not yet built gets no factory and stays
/// silent — which is the seam's own headless behaviour, and exactly
/// what the Mac workstation replaces when it adds its sinks.
/// </summary>
public static class MauiAudioBackend
{
    public static void Install()
    {
#if WINDOWS
        BuzzerEngine.Install(() => new WindowsAudioSink());
#endif
        // Mac Catalyst / iOS: AVAudioEngine player nodes; Android:
        // AudioTrack (D18) — added with their lanes on the Mac
        // workstation. B3.7 holds per shipped platform: a lane does not
        // ship until its sink exists.
    }

    /// <summary>Drain-and-stop, for app teardown.</summary>
    public static void Shutdown() => BuzzerEngine.Shutdown(drain: true);
}
