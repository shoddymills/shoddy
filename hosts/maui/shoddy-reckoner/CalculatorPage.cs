// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using Shoddy.Hosting;
using Shoddy.Maui;

namespace ShoddyReckoner;

/// <summary>
/// The calculator's one screen: the unified terminal control as the
/// display, the native Entry as the input line. The split is exactly
/// where the device differences live — the drawn grid renders the same
/// pixels on every platform, while prose typing keeps the platform's
/// keyboard, IME, clipboard and screen reader. The core still runs off
/// the UI thread; QUIT still closes the window; Cancel still restarts
/// the session, QUIT-style, and says so.
/// </summary>
public sealed class CalculatorPage : ContentPage
{
    readonly TerminalView term = new();
    readonly Entry entry;
    readonly ActivityIndicator spinner;
    readonly Button cancel;
    HalifaxSession? session;

    public CalculatorPage()
    {
        Title = "Shoddy Reckoner";
        Padding = 8;

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
        cancel.Clicked += (_, _) => Cancel();

        var grid = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition(GridLength.Star),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
            },
            RowSpacing = 4,
        };
        grid.Add(term, 0, 0);
        grid.Add(new HorizontalStackLayout { Spacing = 8, Children = { spinner, cancel } }, 0, 1);
        grid.Add(entry, 0, 2);
        Content = grid;

        Loaded += (_, _) => _ = OpenSessionAsync();
    }

    bool Busy
    {
        set
        {
            spinner.IsRunning = spinner.IsVisible = cancel.IsVisible = value;
            entry.IsEnabled = !value;
            if (!value) entry.Focus();
        }
    }

    void Show(IEnumerable<string> lines)
    {
        string text = string.Join("\r\n", lines);
        if (text.Length == 0) return;
        term.Grid.Feed(text + "\r\n");
        term.FollowTail();
    }

    /// <summary>Persisted files — halifaxrc, SAVE and LOAD paths, tapes
    /// — live under app storage, per mill (B3.5, D9): the mill never
    /// chooses where the filesystem begins. The process working
    /// directory is set to the same root so the file MACHINE's words,
    /// running inside the engine, resolve there too — the runtime is
    /// unchanged (B3.1) and resolves against the current directory.</summary>
    static string FileRoot()
    {
        string root = Path.Combine(FileSystem.AppDataDirectory, "halifax");
        Directory.CreateDirectory(root);
        Directory.SetCurrentDirectory(root);
        return root;
    }

    async Task OpenSessionAsync()
    {
        if (session != null) return;
        Busy = true;
        try
        {
            HalifaxSession opened = await Task.Run(() =>
                HalifaxSession.Open(new ShoddyHostOptions
                {
                    FileRoot = FileRoot(),
                    // halifax's grant includes `net` (B4.8); the user
                    // names every URL (B4.6), the app ships none.
                    AllowNet = true,
                    // The engine's own output — seedterminal's PRINT —
                    // streams into the same face, live, mid-turn: the
                    // resource behind the `terminal` capability.
                    Output = new TerminalWriter(term.Grid),
                }));
            session = opened;
            Show(await Task.Run(opened.Opening));
            entry.Placeholder = opened.Prompt;
        }
        catch (Exception e)
        {
            Show(new[] { "the calculator could not start: " + e.Message });
        }
        finally
        {
            Busy = false;
        }
    }

    void Submit()
    {
        if (session is null) return;
        string line = entry.Text ?? "";
        entry.Text = "";
        // The typed line joins the transcript the way the terminal's
        // echo does, wearing the prompt it was typed at.
        term.Grid.Feed(session.Prompt + line + "\r\n");
        term.FollowTail();
        _ = RunTurnAsync(line);
    }

    async Task RunTurnAsync(string line)
    {
        if (session is null) return;
        Busy = true;
        try
        {
            HalifaxTurn? turn = await session.SubmitAsync(line);
            if (turn is null) return;   // abandoned mid-flight; Cancel already spoke
            if (turn.Quit)
            {
                Application.Current?.Quit();
                return;
            }
            Show(turn.Shown);
            entry.Placeholder = turn.Prompt;
        }
        catch (Exception e)
        {
            // A word's own abort (Error(msg)) surfaces here undecorated;
            // the session and its state are intact — show it as the
            // terminal would and carry on.
            Show(new[] { e.Message });
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>B4.11's cancel: walk away from a turn that is not
    /// coming back. The session restarts — the terminal's own
    /// QUIT-and-relaunch semantics: halifaxrc reloads, unsaved words
    /// are gone unless SAVE put them somewhere — and the transcript
    /// says exactly that.</summary>
    void Cancel()
    {
        if (session is null) return;
        IReadOnlyList<string> reopened = session.Abandon();
        Show(new[] { "cancelled — the session restarted; unsaved words are gone (SAVE writes them to a file)" });
        Show(reopened);
        entry.Placeholder = session.Prompt;
        Busy = false;
    }
}
