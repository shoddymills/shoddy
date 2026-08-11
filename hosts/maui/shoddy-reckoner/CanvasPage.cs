// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using Shoddy.Maui;
using Shoddy.Runtime;

namespace ShoddyReckoner;

/// <summary>
/// A page that HOLDS a drawing (B1.4b): the scribbler canvas laid out
/// by MAUI with ordinary chrome around it — a picture inside the app's
/// UI, never the app's UI. Opened when the calculator draws (a plotter
/// chart, a turtle drawing — B4.5), closed by the navigation bar's own
/// back affordance or by the mill's SCRIBBLERCLOSE, whichever comes
/// first; MarkClosed arbitrates so the two cannot double-count.
/// </summary>
public sealed class CanvasPage : ContentPage
{
    readonly ScribblerHandle handle;

    public CanvasPage(ScribblerCanvasView view, ScribblerHandle handle)
    {
        this.handle = handle;
        Title = "Drawing";
        BackgroundColor = Colors.Black;
        Content = view;
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        // The user navigated away while the mill still holds the
        // scribbler: same bookkeeping as a window close at the desktop.
        MauiScribblerBackend.NoteDismissed(handle);
    }
}

/// <summary>
/// Installs the two content backends at startup (B1.4a): the scribbler
/// canvas — presented as a pushed CanvasPage — and the buzzer. Content
/// surfaces only; no app screen is drawn through either (B1.4).
/// </summary>
public static class Backends
{
    static readonly Dictionary<ScribblerCanvasView, CanvasPage> open = new();

    public static void Install()
    {
        MauiScribblerBackend.Install(
            present: (view, handle) =>
            {
                var page = new CanvasPage(view, handle);
                open[view] = page;
                _ = Shell.Current.Navigation.PushAsync(page);
            },
            dismiss: view =>
            {
                if (!open.Remove(view, out CanvasPage? page)) return;
                if (Shell.Current.Navigation.NavigationStack.LastOrDefault() == page)
                    _ = Shell.Current.Navigation.PopAsync();
            });
        MauiAudioBackend.Install();
    }
}
