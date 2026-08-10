// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Collections.ObjectModel;

namespace Shoddy.Maui;

/// <summary>
/// The terminal-like face, as a dumb view (B4.7): a scrolling transcript
/// of lines and a single-line entry beneath it, both ordinary MAUI
/// controls — not a terminal emulator, not a bitmap, not a scribbler
/// surface. It holds no calculator state and runs no engine: a page
/// wires <see cref="LineSubmitted"/> to whatever pump it owns —
/// HalifaxSession for the calculator, RunWovenAsync's pipes for a
/// terminal game (B5.4) — which is why the shelf can reuse it outright.
///
/// The busy row is B4.11 made visible: while a line is away at the
/// core the entry is disabled, the indicator spins, and Cancel is the
/// one live control.
/// </summary>
public sealed class TranscriptView : ContentView
{
    readonly ObservableCollection<string> lines = new();
    readonly CollectionView list;
    readonly Entry entry;
    readonly ActivityIndicator spinner;
    readonly Button cancel;

    /// <summary>Raised on the UI thread with the submitted text; the
    /// entry is already cleared and the view already busy.</summary>
    public event Action<string>? LineSubmitted;

    /// <summary>The busy row's one live control (B4.11).</summary>
    public event Action? CancelRequested;

    public TranscriptView()
    {
        list = new CollectionView
        {
            ItemsSource = lines,
            SelectionMode = SelectionMode.None,
            // A transcript reads at its tail: new lines keep the view
            // pinned to the bottom, the way a terminal scrolls.
            ItemsUpdatingScrollMode = ItemsUpdatingScrollMode.KeepLastItemInView,
            ItemTemplate = new DataTemplate(() =>
            {
                var label = new Label
                {
                    FontFamily = "Consolas",
                    FontSize = 14,
                    LineBreakMode = LineBreakMode.CharacterWrap,
                };
                label.SetBinding(Label.TextProperty, ".");
                return label;
            }),
        };

        entry = new Entry
        {
            FontFamily = "Consolas",
            FontSize = 14,
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
            ReturnType = ReturnType.Send,
        };
        entry.Completed += (_, _) => Submit();

        spinner = new ActivityIndicator { IsRunning = false, IsVisible = false };
        cancel = new Button { Text = "Cancel", IsVisible = false };
        cancel.Clicked += (_, _) => CancelRequested?.Invoke();

        var busyRow = new HorizontalStackLayout
        {
            Spacing = 8,
            Children = { spinner, cancel },
        };

        Content = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition(GridLength.Star),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
            },
            RowSpacing = 4,
        };
        var grid = (Grid)Content;
        grid.Add(list, 0, 0);
        grid.Add(busyRow, 0, 1);
        grid.Add(entry, 0, 2);
    }

    /// <summary>The prompt labels the entry (D9): HxPrompt at rest,
    /// HxMorePrompt while a definition is open, so a half-typed
    /// definition is visible as such.</summary>
    public string Prompt
    {
        get => entry.Placeholder ?? "";
        set => entry.Placeholder = value;
    }

    public bool Busy
    {
        get => spinner.IsRunning;
        set
        {
            spinner.IsRunning = spinner.IsVisible = cancel.IsVisible = value;
            entry.IsEnabled = !value;
            if (!value) entry.Focus();
        }
    }

    /// <summary>Append transcript lines and keep the newest visible.</summary>
    public void Append(IEnumerable<string> more)
    {
        foreach (string line in more) lines.Add(line);
        ScrollToTail();
    }

    /// <summary>Deferred, not immediate: a ScrollTo issued in the same
    /// breath as the Add runs before the new item has been measured,
    /// and the view ends up at the top. Dispatch puts it after
    /// layout.</summary>
    void ScrollToTail()
    {
        if (lines.Count == 0) return;
        Dispatcher.Dispatch(() =>
            list.ScrollTo(lines.Count - 1, position: ScrollToPosition.End, animate: false));
    }

    void Submit()
    {
        string line = entry.Text ?? "";
        entry.Text = "";
        // The typed line joins the transcript the way the terminal's
        // echo does, wearing the prompt it was typed at.
        lines.Add(Prompt + line);
        ScrollToTail();
        LineSubmitted?.Invoke(line);
    }
}
