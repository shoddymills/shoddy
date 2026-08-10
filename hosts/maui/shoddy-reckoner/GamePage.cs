// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Reflection;
using Shoddy.Hosting;
using Shoddy.Maui;

namespace ShoddyReckoner;

/// <summary>
/// A terminal game's page: a woven Mode T mill running whole and
/// unmodified through the terminal control — the same DLL `mill run`
/// executes at a console. Two shapes share this class, split exactly
/// where the input models split:
///
/// RAW (pac): a fixed-size grid, keystrokes captured by the terminal
/// itself and fed character-by-character — INKEY's world.
///
/// LINE (oregon, mungo): an auto-sized scrolling transcript with the
/// calculator's native Entry beneath — Input's world, with the
/// platform's keyboard, IME and clipboard kept. Local echo is per
/// game: oregon expects the terminal to echo; mungo echoes piped
/// commands itself, and echoing twice would double every line.
///
/// Lifecycle, stated because it is deliberate: navigating away does
/// not stop the game — cancellation is cooperative and these programs
/// poll or block on their own terms — so it keeps running until it
/// ends or the app closes. Coming back finds it where it was left.
/// When the mill's Main returns, a "play again" button appears.
/// </summary>
public class GamePage : ContentPage
{
    /// <summary>Field notes for live-play debugging, at a fixed path so
    /// a session can be read back without hunting app storage. Spike
    /// scaffolding — retire it with the shelf's polish pass.</summary>
    internal static void Log(string line)
    {
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "shoddy-reckoner-shelf.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {line}\r\n");
        }
        catch { /* diagnostics never break play */ }
    }

    readonly string woven;
    readonly int cols, rows;                 // 0,0 = auto-size (line games)
    readonly bool rawKeys, echo;
    readonly string[] gameArgs;
    readonly Grid layout;
    readonly Label hint;
    readonly Button again;
    Entry? entry;
    TerminalView? term;
    TerminalInputQueue? keys;
    Task<int>? run;

    protected GamePage(string title, string woven, string hint,
        bool rawKeys, int cols = 0, int rows = 0, bool echo = false,
        string[]? gameArgs = null)
    {
        this.woven = woven;
        this.rawKeys = rawKeys;
        this.cols = cols;
        this.rows = rows;
        this.echo = echo;
        this.gameArgs = gameArgs ?? Array.Empty<string>();

        Title = title;
        Padding = 8;

        this.hint = new Label
        {
            Text = hint,
            FontFamily = "Consolas",
            FontSize = 12,
            VerticalOptions = LayoutOptions.Center,
        };
        again = new Button { Text = "Play again", IsVisible = false };
        again.Clicked += (_, _) => Start();

        layout = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition(GridLength.Star),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
            },
            RowSpacing = 4,
        };
        layout.Add(new HorizontalStackLayout { Spacing = 12, Children = { again, this.hint } }, 0, 1);
        if (!rawKeys)
        {
            entry = new Entry
            {
                FontFamily = "Consolas",
                FontSize = 14,
                IsSpellCheckEnabled = false,
                IsTextPredictionEnabled = false,
                ReturnType = ReturnType.Send,
            };
            entry.Completed += (_, _) => Submit();
            entry.TextChanged += (_, e) => Log($"{woven}: entry text now '{e.NewTextValue}'");
            entry.Focused += (_, _) => Log($"{woven}: entry focused");
            entry.Unfocused += (_, _) => Log($"{woven}: entry unfocused");
            // Focus is claimed when the platform control actually
            // exists — OnAppearing runs before the handler does, and a
            // Focus() then returns false and lands nowhere.
            entry.Loaded += (_, _) => Log($"{woven}: entry.Loaded Focus() -> {entry.Focus()}");
            layout.Add(entry, 0, 2);
        }
        Content = layout;

        Loaded += (_, _) => { if (run is null) Start(); };
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Log($"{woven}: page appearing (raw={rawKeys})");
        // Deferred one turn of the dispatcher: by then the handlers
        // exist and focus sticks — immediately, it reports false.
        Dispatcher.Dispatch(() =>
        {
            if (rawKeys) term?.FocusKeyboard();
            else if (entry != null) Log($"{woven}: deferred entry.Focus() -> {entry.Focus()}");
        });
    }

    void Start()
    {
        again.IsVisible = false;
        if (term != null) layout.Remove(term);

        var grid = cols > 0 ? new TerminalGrid(cols, rows) : new TerminalGrid();
        keys = new TerminalInputQueue();
        term = new TerminalView(grid)
        {
            AutoResize = cols == 0,
            Keys = rawKeys ? keys : null,
            // The line games type below the transcript — a block cursor
            // in it points at the one place you cannot type.
            ShowCursor = rawKeys,
        };
        if (!rawKeys)
        {
            // A click on the transcript reads as "let me type" — hand
            // the keyboard to the input line, the way the raw pages
            // hand it to the drawn board.
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => entry?.Focus();
            term.GestureRecognizers.Add(tap);
        }
        layout.Add(term, 0, 0);
        if (entry != null) entry.IsEnabled = true;

        try
        {
            run = ShoddyHost.RunWovenAsync(Assembly.Load(woven),
                new TerminalWriter(grid), keys, gameArgs);
            Log($"{woven}: run started");
        }
        catch (Exception e)
        {
            Log($"{woven}: could not start — {e.Message}");
            hint.Text = "the game could not start: " + e.Message;
            return;
        }
        _ = WatchAsync(run, grid);
    }

    void Submit()
    {
        Log($"{woven}: submit fired (entry={(entry != null)}, keys={(keys != null)}, term={(term != null)})");
        if (entry is null || keys is null || term is null) return;
        string line = entry.Text ?? "";
        entry.Text = "";
        Log($"{woven}: line enqueued '{line}'");
        // Local echo where the game expects the terminal to provide it —
        // the typed line joins the transcript at the prompt it answered.
        if (echo) term.Grid.Feed(line + "\r\n");
        keys.Enqueue(line + "\r\n");
        term.FollowTail();
    }

    async Task WatchAsync(Task<int> game, TerminalGrid grid)
    {
        try
        {
            int exit = await game;
            // An ending is always said out loud — a run that died the
            // moment it started must never masquerade as a frozen one.
            Log($"{woven}: run ended, exit {exit}");
            grid.Feed("\r\n— the game ended (exit " + exit + ") —\r\n");
        }
        catch (Exception e)
        {
            // A woven program's own abort (Error(msg)) surfaces here;
            // the terminal shows it where the game stood.
            Log($"{woven}: run stopped — {e.Message}");
            grid.Feed("\r\nthe game stopped: " + e.Message + "\r\n");
        }
        again.IsVisible = true;
        if (entry != null) entry.IsEnabled = false;
    }
}

/// <summary>
/// A canvas game's page: the woven mill opens its scribbler and the
/// installed presenter pushes the drawing as its own page, with the
/// event plumbing attached — this page only starts the run, shows any
/// console output the game prints, and offers the replay. Closing the
/// drawing (back-navigation) sends the game its Quit, its loop exits,
/// and the button appears here.
/// </summary>
public class CanvasGamePage : ContentPage
{
    readonly string woven;
    readonly Label hint;
    readonly Button again;
    readonly TerminalView transcript;
    Task<int>? run;

    protected CanvasGamePage(string title, string woven, string hint)
    {
        this.woven = woven;
        Title = title;
        Padding = 8;

        this.hint = new Label
        {
            Text = hint,
            FontFamily = "Consolas",
            FontSize = 12,
            VerticalOptions = LayoutOptions.Center,
        };
        again = new Button { Text = "Play again", IsVisible = false };
        again.Clicked += (_, _) => Start();
        transcript = new TerminalView();

        // The whole audio question, one click: this drives the seam the
        // game drives, with nothing woven in between — if this beeps
        // and the game is silent, the woven path is the suspect; if
        // this is silent too, the sink is.
        var beep = new Button { Text = "Beep" };
        beep.Clicked += (_, _) => _ = BeepAsync();

        var layout = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition(GridLength.Star),
                new RowDefinition(GridLength.Auto),
            },
            RowSpacing = 4,
        };
        layout.Add(transcript, 0, 0);
        layout.Add(new HorizontalStackLayout { Spacing = 12, Children = { again, beep, this.hint } }, 0, 1);
        Content = layout;

        Loaded += (_, _) => { if (run is null) Start(); };
    }

    void Start()
    {
        again.IsVisible = false;
        try
        {
            // The input pipe stays open and empty: a canvas game reads
            // the scribbler's event queue, never the console.
            run = ShoddyHost.RunWovenAsync(Assembly.Load(woven),
                new TerminalWriter(transcript.Grid), new TerminalInputQueue(),
                Array.Empty<string>());
        }
        catch (Exception e)
        {
            hint.Text = "the game could not start: " + e.Message;
            return;
        }
        _ = WatchAsync(run);
        _ = ProbeAudioAsync();
    }

    /// <summary>A sound game that cannot sound should say so, not
    /// shrug: by two seconds in, the fanfare has forced the sink to
    /// init, and whatever the seam's state is goes on the transcript —
    /// healthy, unasked, or failed alike.</summary>
    async Task ProbeAudioAsync()
    {
        await Task.Delay(2500);
        string state = Shoddy.Runtime.BuzzerEngine.State;
        GamePage.Log($"{woven}: audio {state}");
        transcript.Grid.Feed("audio: " + state + "\r\n");
    }

    /// <summary>Off the UI thread, because the first beep may create
    /// the sink, and blocking device creation belongs on a worker.</summary>
    async Task BeepAsync()
    {
        await Task.Run(() => Shoddy.Runtime.BuzzerRegistry.Sound?.Invoke(880, 250));
        string state = Shoddy.Runtime.BuzzerEngine.State;
        GamePage.Log($"{woven}: beep sent, audio {state}");
        transcript.Grid.Feed("beep sent · audio: " + state + "\r\n");
    }

    async Task WatchAsync(Task<int> game)
    {
        try
        {
            await game;
        }
        catch (Exception e)
        {
            transcript.Grid.Feed("\r\nthe game stopped: " + e.Message + "\r\n");
        }
        again.IsVisible = true;
    }
}

// ---- the shelf -------------------------------------------------------

public sealed class PacPage : GamePage
{
    public PacPage() : base("Pac-Man", "pac",
        "WASD or arrows steer · Q or Esc quits · click the board if keys go quiet",
        rawKeys: true, cols: 80, rows: 26)
    { }
}

public sealed class OregonPage : GamePage
{
    public OregonPage() : base("Oregon Trail", "oregon",
        "type an answer and press Enter · when told to TYPE a word, speed is marksmanship",
        rawKeys: false, echo: true)
    { }
}

public sealed class MungoPage : GamePage
{
    public MungoPage() : base("Mungo Caverns", "mungo-caverns",
        "type a command and press Enter · SAVE writes to the app's own files",
        // -o is the mill's own old-style switch: no "> " chevron, so
        // the transcript reads as a dialogue with no hanging prompt.
        rawKeys: false, echo: false, gameArgs: new[] { "-o" })
    { }
}

public sealed class InvadersPage : CanvasGamePage
{
    public InvadersPage() : base("Invaders", "invaders",
        "arrows move · Space fires · Q or Esc quits · closing the drawing quits too")
    { }
}

public sealed class DevilsDustPage : CanvasGamePage
{
    public DevilsDustPage() : base("Devil's Dust", "devils-dust",
        "mouse works the panel · 1-4 tabs, arrows pick and nudge · Q or Esc quits")
    { }
}
