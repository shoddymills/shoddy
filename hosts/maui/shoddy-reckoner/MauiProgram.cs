// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using Microsoft.Extensions.Logging;
using SkiaSharp.Views.Maui.Controls.Hosting;

namespace ShoddyReckoner;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            // The one third-party SDK, for the scribbler canvas only
            // (D10): plotter charts, turtle drawings, nothing else.
            .UseSkiaSharp()
            .ConfigureFonts(fonts =>
            {
                // The brand's Besley semibold (docs headings), for the
                // app's own chrome; the transcript stays monospace.
                fonts.AddFont("Besley-SemiBold.ttf", "Besley");
            });

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
