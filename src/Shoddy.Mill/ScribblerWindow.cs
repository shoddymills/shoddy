// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using Shoddy.Runtime;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace Shoddy.Mill;

/// <summary>One scribbler window: a Silk.NET IWindow plus its input
/// context and one texture sized to the scribbler. OpenGL is purely a
/// presentation mechanism — upload the runtime's software pixel buffer as
/// one texture, draw one fullscreen textured triangle, done. Nothing is
/// GPU-rendered. Constructed, pumped and destroyed on the main thread
/// only; the handle callbacks it installs are the crossings.</summary>
public sealed class ScribblerWindow
{
    const int QueueCap = 2048;      // bounded and lossy: drop oldest on overflow

    public readonly ScribblerHandle Handle;

    readonly MainThreadDispatcher dispatcher;
    readonly IWindow window;
    readonly IInputContext input;
    readonly IKeyboard? keyboard;
    readonly GL gl;
    readonly uint texture, shader, vao;

    readonly string glsl;           // #version line matching the context we got

    volatile bool dirty;            // OnBlit (Shoddy thread) sets; pump clears
    bool destroyed;                 // main thread only
    int intervalMs;                 // main thread only (set via posted action)
    double nextTickAt;

    // Producer-side coalescing state (event callbacks run on the main
    // thread during pumping, so these need no locks).
    ScribblerEvent? lastEnqueued;
    ScribblerEvent? lastTick;

    /// <summary>The context to ask for, and the GLSL version that goes with
    /// it. Linux asks for 3.1; macOS and Windows keep 3.3 core
    /// forward-compatible.
    ///
    /// Linux is the exception because of the Raspberry Pi: the v3d driver
    /// (Pi 4 and 5) tops out at desktop GL 3.1, and vc4 (Pi 3 and earlier)
    /// at 2.1, so a 3.3 request cannot be met there. Desktop Mesa satisfies
    /// 3.1 without complaint, so one Linux branch covers both. macOS keeps
    /// 3.3 because Apple offers 2.1 legacy or 3.2+ core and nothing in
    /// between — 3.1 is not merely lower there, it does not exist. Windows
    /// keeps it because its vendor drivers have always answered 3.3 core and
    /// there is nothing to gain by asking for less on a platform where the
    /// failure would be invisible until a user hit it.
    ///
    /// This is a decision, not a probe, and it has to be: GLFW does not
    /// throw when it cannot honour a context request — Silk.NET calls on
    /// through the null window and the process dies in native code with no
    /// managed frame to catch. (Verified twice over: 3.3 core segfaults on a
    /// Pi, and 3.1 segfaults on macOS.) There is no fallback ladder to
    /// write, so the first request must be one the driver can actually meet.
    ///
    /// GLSL 1.40 keeps everything the presentation path needs — gl_VertexID,
    /// VAOs, texture(), in/out, integer bit operators — so only the #version
    /// line differs between the two.</summary>
    static (GraphicsAPI Api, string Glsl) ContextChoice() =>
        OperatingSystem.IsLinux()
            ? (new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Compatability,
                               ContextFlags.Default, new APIVersion(3, 1)), "#version 140")
            : (new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core,
                               ContextFlags.ForwardCompatible, new APIVersion(3, 3)), "#version 330 core");

    public ScribblerWindow(MainThreadDispatcher dispatcher, int width, int height)
    {
        this.dispatcher = dispatcher;

        (GraphicsAPI api, string version) = ContextChoice();
        glsl = version;
        // IsVisible is settled at creation and never toggled: a window
        // created shown and hidden a moment later has already flashed on
        // screen, which is exactly what --no-window exists to prevent. The
        // GL context, the buffer and SCRIBBLERSAVE all work unchanged
        // behind a hidden window — only the presenting stops. (A machine
        // with no display at all is still a machine with no display: GLFW
        // cannot initialize there, hidden or not.)
        WindowOptions opts = WindowOptions.Default with
        {
            Size = new Vector2D<int>(width, height),
            Title = "Scribbler",
            API = api,
            WindowBorder = WindowBorder.Fixed,
            VSync = false,
            ShouldSwapAutomatically = false,
            IsVisible = !ScribblerWindows.Hidden,
        };
        window = Window.Create(opts);
        window.Initialize();
        window.FramebufferResize += _ => dirty = true;

        input = window.CreateInput();
        keyboard = input.Keyboards.Count > 0 ? input.Keyboards[0] : null;
        WireInput();

        gl = GL.GetApi(window);
        window.GLContext!.MakeCurrent();
        (texture, shader, vao) = CreateGlResources(width, height);

        var handle = new ScribblerHandle
        {
            Width = width,
            Height = height,
            Pixels = new byte[width * height * 4],
        };
        // OnBlit runs on the Shoddy thread: GL calls are affine to this
        // context, so it only marks dirty and wakes the pump; the actual
        // glTexSubImage2D and redraw happen during the next main-thread
        // pump. (The upload may race later buffer writes — at worst one
        // torn frame, corrected by the next blit; single-buffer software
        // rendering accepts this.)
        handle.OnBlit = (_, _, _) => { dirty = true; dispatcher.Wake(); };
        // The runtime has already done the MarkClosed bookkeeping when it
        // invokes OnClose — this is only "destroy the window, please".
        handle.OnClose = () => dispatcher.Post(Destroy);
        handle.OnSetTitle = t => dispatcher.Post(() => { if (!destroyed) window.Title = t; });
        // A request, not a command: window managers on every platform are
        // entitled to place a window where they like, and tiling ones
        // routinely do. Nothing reads the position back to check.
        handle.OnPlace = (x, y) => dispatcher.Post(() =>
        {
            if (!destroyed) window.Position = new Vector2D<int>(x, y);
        });
        handle.OnSetInterval = ms => dispatcher.Post(() =>
        {
            intervalMs = ms;
            nextTickAt = Ticker.Now + ms;
        });
        Handle = handle;

        ScribblerWindows.Register(this);
    }

    /// <summary>The next moment this window wants the pump to wake it
    /// (its pending interval tick), or null when no interval is set.</summary>
    public double? NextDeadline => intervalMs > 0 && !destroyed ? nextTickAt : null;

    /// <summary>One main-loop iteration's worth of servicing: pump GLFW
    /// events, emit a due interval tick, notice a user-initiated close,
    /// redraw if the buffer was blitted since last time.</summary>
    public void Pump()
    {
        if (destroyed) return;
        window.DoEvents();
        if (destroyed) return;              // a callback may have closed us
        if (window.IsClosing) { UserClose(); return; }

        double now = Ticker.Now;
        if (intervalMs > 0 && now >= nextTickAt)
        {
            EmitTick(now);
            while (nextTickAt <= now) nextTickAt += intervalMs;   // skip missed ticks
        }
        if (dirty) Redraw();
    }

    /// <summary>The user closed the window (title-bar button, Cmd-W). Push
    /// a Quit event so a Wait loop reaches its exit branch, then the same
    /// teardown SCRIBBLERCLOSE would do — MarkClosed arbitrates so the
    /// OpenCount decrement and teardown Signal release happen exactly once
    /// no matter which side gets there first.</summary>
    void UserClose()
    {
        Enqueue(new ScribblerEvent { Type = ScribblerEvent.Kind.Quit, At = Ticker.Now });
        if (Handle.MarkClosed())
        {
            ScribblerRegistry.NoteClosed();
            Handle.Signal.Release();        // teardown release — wake any waiter
        }
        Destroy();
    }

    /// <summary>Program-error teardown: close without a Quit event (the
    /// program is already gone).</summary>
    public void ForceClose()
    {
        if (Handle.MarkClosed())
        {
            ScribblerRegistry.NoteClosed();
            Handle.Signal.Release();
        }
        Destroy();
    }

    void Destroy()
    {
        if (destroyed) return;
        destroyed = true;
        ScribblerWindows.Unregister(this);
        window.GLContext!.MakeCurrent();
        gl.DeleteVertexArray(vao);
        gl.DeleteProgram(shader);
        gl.DeleteTexture(texture);
        input.Dispose();
        window.Dispose();
    }

    // ---- events ---------------------------------------------------------

    void WireInput()
    {
        if (input.Mice.Count > 0)
        {
            IMouse mouse = input.Mice[0];
            mouse.MouseDown += (m, btn) => EnqueueMouse(ScribblerEvent.Kind.MouseDown, m, btn);
            mouse.MouseUp += (m, btn) => EnqueueMouse(ScribblerEvent.Kind.MouseUp, m, btn);
            mouse.MouseMove += (m, pos) => EnqueueMove((int)pos.X, (int)pos.Y);
        }
        if (keyboard != null)
        {
            // GLFW auto-repeat, decided explicitly: repeat KeyDowns are
            // passed through as further KeyDown events whenever the input
            // backend forwards them — deliberately unfiltered, since a
            // text field wants held-Backspace to repeat. Typed events
            // always repeat regardless: GLFW's char callback re-fires on
            // key repeat, and that is where KeyChar comes from (layout,
            // shift and IME handled by the platform, never derived from
            // the physical key).
            keyboard.KeyDown += (_, key, _) => EnqueueKey(ScribblerEvent.Kind.KeyDown, key);
            keyboard.KeyUp += (_, key, _) => EnqueueKey(ScribblerEvent.Kind.KeyUp, key);
            keyboard.KeyChar += (_, ch) => Enqueue(new ScribblerEvent
            {
                Type = ScribblerEvent.Kind.Typed,
                KeyChar = ch.ToString(),
                Mods = CurrentMods(),
                At = Ticker.Now,
            });
        }
    }

    void EnqueueMouse(ScribblerEvent.Kind kind, IMouse mouse, MouseButton btn) =>
        Enqueue(new ScribblerEvent
        {
            Type = kind,
            X = (int)mouse.Position.X,
            Y = (int)mouse.Position.Y,
            Button = btn switch
            {
                MouseButton.Left => 1,
                MouseButton.Right => 2,
                MouseButton.Middle => 3,
                _ => 0,
            },
            Mods = CurrentMods(),
            At = Ticker.Now,
        });

    /// <summary>Consecutive MouseMoves coalesce: if the most recently
    /// enqueued event is a still-queued MouseMove, update it in place —
    /// only the newest position matters, and without this a minute of
    /// mouse waving leaves thousands of stale events to chew through.</summary>
    void EnqueueMove(int x, int y)
    {
        ScribblerEvent? last = lastEnqueued;
        if (last != null && last.Type == ScribblerEvent.Kind.MouseMove && !last.Taken)
        {
            last.X = x; last.Y = y; last.Mods = CurrentMods(); last.At = Ticker.Now;
            return;
        }
        Enqueue(new ScribblerEvent
        {
            Type = ScribblerEvent.Kind.MouseMove,
            X = x, Y = y,
            Mods = CurrentMods(),
            At = Ticker.Now,
        });
    }

    void EnqueueKey(ScribblerEvent.Kind kind, Key key) =>
        Enqueue(new ScribblerEvent
        {
            Type = kind,
            Key = MapKey(key),
            Mods = CurrentMods(),
            At = Ticker.Now,
        });

    /// <summary>At most one pending Tick: if the handler cannot keep up,
    /// missed ticks are dropped rather than queued — one slow frame must
    /// not spiral into an ever-growing backlog. The program can see the
    /// drop in the At timestamp.</summary>
    void EmitTick(double now)
    {
        if (lastTick != null && !lastTick.Taken) return;
        var t = new ScribblerEvent { Type = ScribblerEvent.Kind.Tick, At = now };
        lastTick = t;
        Enqueue(t);
    }

    void Enqueue(ScribblerEvent ev)
    {
        while (Handle.Events.Count >= QueueCap && Handle.Events.TryDequeue(out _)) { }
        Handle.Events.Enqueue(ev);
        lastEnqueued = ev;
        Handle.Signal.Release();            // once per enqueue, always
    }

    ScribblerEvent.Mod CurrentMods()
    {
        // Modifier state is the keyboard's currently-pressed set at the
        // moment the event is built.
        ScribblerEvent.Mod m = ScribblerEvent.Mod.None;
        if (keyboard == null) return m;
        if (keyboard.IsKeyPressed(Key.ShiftLeft) || keyboard.IsKeyPressed(Key.ShiftRight))
            m |= ScribblerEvent.Mod.Shift;
        if (keyboard.IsKeyPressed(Key.ControlLeft) || keyboard.IsKeyPressed(Key.ControlRight))
            m |= ScribblerEvent.Mod.Ctrl;
        if (keyboard.IsKeyPressed(Key.AltLeft) || keyboard.IsKeyPressed(Key.AltRight))
            m |= ScribblerEvent.Mod.Alt;
        if (keyboard.IsKeyPressed(Key.SuperLeft) || keyboard.IsKeyPressed(Key.SuperRight))
            m |= ScribblerEvent.Mod.Super;
        return m;
    }

    /// <summary>Silk.NET's Key enum mapped onto Shoddy's own stable
    /// ScribblerKeys codes — the library enum never crosses the seam.
    /// Anything unmapped reports 0.</summary>
    static int MapKey(Key k)
    {
        if (k >= Key.A && k <= Key.Z) return 'A' + (k - Key.A);
        if (k >= Key.Number0 && k <= Key.Number9) return '0' + (k - Key.Number0);
        if (k >= Key.F1 && k <= Key.F12) return ScribblerKeys.F1 + (k - Key.F1);
        return k switch
        {
            Key.Enter or Key.KeypadEnter => ScribblerKeys.Enter,
            Key.Escape => ScribblerKeys.Escape,
            Key.Backspace => ScribblerKeys.Backspace,
            Key.Tab => ScribblerKeys.Tab,
            Key.Space => ScribblerKeys.Space,
            Key.Delete => ScribblerKeys.Delete,
            Key.Insert => ScribblerKeys.Insert,
            Key.Left => ScribblerKeys.Left,
            Key.Right => ScribblerKeys.Right,
            Key.Up => ScribblerKeys.Up,
            Key.Down => ScribblerKeys.Down,
            Key.Home => ScribblerKeys.Home,
            Key.End => ScribblerKeys.End,
            Key.PageUp => ScribblerKeys.PageUp,
            Key.PageDown => ScribblerKeys.PageDown,
            Key.ShiftLeft => ScribblerKeys.LeftShift,
            Key.ShiftRight => ScribblerKeys.RightShift,
            Key.ControlLeft => ScribblerKeys.LeftCtrl,
            Key.ControlRight => ScribblerKeys.RightCtrl,
            Key.AltLeft => ScribblerKeys.LeftAlt,
            Key.AltRight => ScribblerKeys.RightAlt,
            Key.SuperLeft => ScribblerKeys.LeftSuper,
            Key.SuperRight => ScribblerKeys.RightSuper,
            _ => 0,
        };
    }

    // ---- presentation ---------------------------------------------------

    (uint Tex, uint Prog, uint Vao) CreateGlResources(int width, int height)
    {
        uint tex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, tex);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8,
                      (uint)width, (uint)height, 0, PixelFormat.Rgba, PixelType.UnsignedByte,
                      (ReadOnlySpan<byte>)new byte[width * height * 4]);

        // Fullscreen triangle from gl_VertexID — no vertex buffer, but core
        // profile still requires a bound VAO. Pixels is row-major top-down
        // while GL texture space is bottom-up, so V flips in the UV here.
        string vsSrc = $$"""
            {{glsl}}
            out vec2 uv;
            void main() {
                vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
                uv = vec2(p.x, 1.0 - p.y);
                gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
            }
            """;
        string fsSrc = $$"""
            {{glsl}}
            in vec2 uv;
            out vec4 color;
            uniform sampler2D tex;
            void main() { color = vec4(texture(tex, uv).rgb, 1.0); }
            """;
        uint prog = gl.CreateProgram();
        uint vs = CompileShader(ShaderType.VertexShader, vsSrc);
        uint fs = CompileShader(ShaderType.FragmentShader, fsSrc);
        gl.AttachShader(prog, vs);
        gl.AttachShader(prog, fs);
        gl.LinkProgram(prog);
        gl.GetProgram(prog, ProgramPropertyARB.LinkStatus, out int linked);
        if (linked == 0)
            throw new InvalidOperationException($"scribbler shader link failed: {gl.GetProgramInfoLog(prog)}");
        gl.DetachShader(prog, vs);
        gl.DetachShader(prog, fs);
        gl.DeleteShader(vs);
        gl.DeleteShader(fs);

        uint v = gl.GenVertexArray();
        return (tex, prog, v);
    }

    uint CompileShader(ShaderType type, string src)
    {
        uint sh = gl.CreateShader(type);
        gl.ShaderSource(sh, src);
        gl.CompileShader(sh);
        gl.GetShader(sh, ShaderParameterName.CompileStatus, out int ok);
        if (ok == 0)
            throw new InvalidOperationException($"scribbler {type} compile failed: {gl.GetShaderInfoLog(sh)}");
        return sh;
    }

    /// <summary>Upload the buffer and draw. This window's context is made
    /// current first — with more than one window open, forgetting that is
    /// the classic multi-window bug, and it fails silently with one.</summary>
    void Redraw()
    {
        dirty = false;                      // clear first: a blit during upload re-marks
        window.GLContext!.MakeCurrent();
        Vector2D<int> fb = window.FramebufferSize;
        gl.Viewport(0, 0, (uint)fb.X, (uint)fb.Y);
        gl.BindTexture(TextureTarget.Texture2D, texture);
        gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0,
                         (uint)Handle.Width, (uint)Handle.Height,
                         PixelFormat.Rgba, PixelType.UnsignedByte,
                         (ReadOnlySpan<byte>)Handle.Pixels);
        gl.UseProgram(shader);
        gl.BindVertexArray(vao);
        gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        window.GLContext.SwapBuffers();
    }
}
