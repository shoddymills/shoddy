// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using Shoddy.Hosting;
using Shoddy.Maui;

namespace ShoddyReckoner;

/// <summary>
/// The calculator's one screen (B4.7, D9): a TranscriptView wired to a
/// HalifaxSession. Submit runs the turn on a worker, the UI thread only
/// ever appends returned lines, and QUIT does what it does at the
/// terminal — ends the session, which for a windowed app means closing
/// the window.
/// </summary>
public sealed class CalculatorPage : ContentPage
{
    readonly TranscriptView transcript = new();
    HalifaxSession? session;

    public CalculatorPage()
    {
        Title = "Shoddy Reckoner";
        Padding = 8;
        Content = transcript;
        transcript.LineSubmitted += line => _ = RunTurnAsync(line);
        transcript.CancelRequested += Cancel;
        Loaded += (_, _) => _ = OpenSessionAsync();
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
        transcript.Busy = true;
        try
        {
            HalifaxSession opened = await Task.Run(() =>
            {
                var s = HalifaxSession.Open(new ShoddyHostOptions
                {
                    FileRoot = FileRoot(),
                    // halifax's grant includes `net` (B4.8); the user
                    // names every URL (B4.6), the app ships none.
                    AllowNet = true,
                });
                return s;
            });
            session = opened;
            IReadOnlyList<string> opening = await Task.Run(opened.Opening);
            transcript.Append(opening);
            transcript.Prompt = opened.Prompt;
        }
        catch (Exception e)
        {
            transcript.Append(new[] { "the calculator could not start: " + e.Message });
        }
        finally
        {
            transcript.Busy = false;
        }
    }

    async Task RunTurnAsync(string line)
    {
        if (session is null) return;
        transcript.Busy = true;
        try
        {
            HalifaxTurn? turn = await session.SubmitAsync(line);
            if (turn is null) return;   // abandoned mid-flight; Cancel already spoke
            if (turn.Quit)
            {
                Application.Current?.Quit();
                return;
            }
            transcript.Append(turn.Shown);
            transcript.Prompt = turn.Prompt;
        }
        catch (Exception e)
        {
            // A word's own abort (Error(msg)) surfaces here undecorated;
            // the session and its state are intact — show it as the
            // terminal would and carry on.
            transcript.Append(new[] { e.Message });
        }
        finally
        {
            transcript.Busy = false;
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
        transcript.Append(new[] { "cancelled — the session restarted; unsaved words are gone (SAVE writes them to a file)" });
        transcript.Append(reopened);
        transcript.Prompt = session.Prompt;
        transcript.Busy = false;
    }
}
