namespace Darkmount.App.Macros;

public enum TriggerResult
{
    /// <summary>The macro started playing.</summary>
    Started,

    /// <summary>A running Toggle macro was asked to stop.</summary>
    Stopped,

    /// <summary>The macro is already running (and is not a Toggle macro), so the press was ignored.</summary>
    Ignored,
}

public enum MacroRunResult { Completed, Stopped, Failed }

/// <summary>
/// Plays macros on background threads. Each run:
/// <list type="bullet">
/// <item>first waits (max <see cref="ModifierReleaseTimeout"/>) until Ctrl/Shift/Alt/Win are physically released, so the
/// trigger's modifiers don't combine with the injected keys;</item>
/// <item>snapshots the step list, so editing a macro never disturbs a running copy;</item>
/// <item>always releases every key/button it pressed and did not release itself — on completion, stop or error.</item>
/// </list>
/// Events are raised on the playback thread; marshal to the UI thread yourself.
/// </summary>
public sealed class MacroPlayer : IDisposable
{
    public const int ModifierPollMs = 10;

    /// <summary>Minimum pause between loop iterations that did not wait at all, so loops can't flood the input queue.</summary>
    public const int MinLoopGapMs = 10;

    public const int MaxRepeatCount = 10_000;

    const int WheelDelta = 120;
    const int MaxScrollNotches = 100;

    readonly IInputSink _input;
    readonly IShell _shell;
    readonly IKeyState _keys;
    readonly IMacroTimer _timer;
    readonly object _gate = new();
    readonly Dictionary<Guid, Run> _runs = [];
    int _active;
    bool _disposed;

    public MacroPlayer(IInputSink input, IShell shell, IKeyState keyState, IMacroTimer? timer = null)
    {
        _input = input;
        _shell = shell;
        _keys = keyState;
        _timer = timer ?? new PreciseTimer();
    }

    /// <summary>Player wired to the real OS: SendInput, the shell, GetAsyncKeyState and a high-resolution timer.</summary>
    public static MacroPlayer CreateDefault() => new(new SendInputSink(), new ShellLauncher(), new AsyncKeyState(), new PreciseTimer());

    /// <summary>How long a run waits for held Ctrl/Shift/Alt to be released before playing anyway.</summary>
    public TimeSpan ModifierReleaseTimeout { get; set; } = TimeSpan.FromSeconds(1);

    public event Action<Macro>? Started;

    /// <summary>Raised once per run, after all keys were released.</summary>
    public event Action<Macro, MacroRunResult>? Finished;

    /// <summary>Raised before <see cref="Finished"/> when a step throws (e.g. <see cref="MacroStepException"/>).</summary>
    public event Action<Macro, Exception>? Failed;

    public bool IsRunning(Guid macroId)
    {
        lock (_gate) return _runs.ContainsKey(macroId);
    }

    public int RunningCount
    {
        get { lock (_gate) return _runs.Count; }
    }

    /// <summary>
    /// Handles a trigger press: starts the macro; stops it if it is a running Toggle macro; otherwise ignores the
    /// press while the macro is still running. Never blocks. Does not check <see cref="Macro.Enabled"/>.
    /// </summary>
    public TriggerResult Trigger(Macro macro)
    {
        ArgumentNullException.ThrowIfNull(macro);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_runs.TryGetValue(macro.Id, out var running))
            {
                if (!running.Toggle || running.Cts.IsCancellationRequested) return TriggerResult.Ignored;
                running.Cts.Cancel();
                return TriggerResult.Stopped;
            }

            long iterations = macro.Mode switch
            {
                PlaybackMode.RepeatCount => Math.Clamp(macro.RepeatCount, 1, MaxRepeatCount),
                PlaybackMode.Toggle => -1,
                _ => 1,
            };
            var run = new Run(macro, [.. (macro.Steps ?? []).Where(s => s is not null)], iterations, macro.Mode == PlaybackMode.Toggle);
            _runs[macro.Id] = run;
            _active++;
            new Thread(() => Execute(run)) { IsBackground = true, Name = "Macro playback" }.Start();
        }
        return TriggerResult.Started;
    }

    /// <summary>Stops one macro (keys are released). Returns false if it wasn't running.</summary>
    public bool Stop(Guid macroId)
    {
        lock (_gate)
        {
            if (!_runs.TryGetValue(macroId, out var run)) return false;
            run.Cts.Cancel();
            return true;
        }
    }

    /// <summary>Panic button: stops every running macro. Returns immediately; see <see cref="WaitForIdle"/>.</summary>
    public void StopAll()
    {
        lock (_gate)
        {
            foreach (var run in _runs.Values) run.Cts.Cancel();
        }
    }

    /// <summary>Waits until no run is active (including its <see cref="Finished"/> handlers). False on timeout.</summary>
    public bool WaitForIdle(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        lock (_gate)
        {
            while (_active > 0)
            {
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || !Monitor.Wait(_gate, left)) return _active == 0;
            }
            return true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        StopAll();
        WaitForIdle(TimeSpan.FromSeconds(2));
    }

    void Execute(Run run)
    {
        var token = run.Cts.Token;
        var result = MacroRunResult.Completed;
        Raise(() => Started?.Invoke(run.Macro));
        try
        {
            WaitForModifierRelease(run);
            for (long i = 0; run.Iterations < 0 || i < run.Iterations; i++)
            {
                if (i > 0 && !run.WaitedThisIteration) Sleep(run, MinLoopGapMs);
                run.WaitedThisIteration = false;
                foreach (var step in run.Steps)
                {
                    token.ThrowIfCancellationRequested();
                    Play(run, step);
                }
                token.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            result = MacroRunResult.Stopped;
        }
        catch (Exception e)
        {
            result = MacroRunResult.Failed;
            Log.Write($"Macro \"{run.Macro.Name}\" failed: {e.Message}");
            Raise(() => Failed?.Invoke(run.Macro, e));
        }
        finally
        {
            ReleaseAll(run);
            lock (_gate) _runs.Remove(run.Macro.Id);
            run.Cts.Dispose();
        }

        Raise(() => Finished?.Invoke(run.Macro, result));
        lock (_gate)
        {
            _active--;
            Monitor.PulseAll(_gate);
        }
    }

    void WaitForModifierRelease(Run run)
    {
        int timeoutMs = (int)Math.Min(ModifierReleaseTimeout.TotalMilliseconds, int.MaxValue);
        for (int waited = 0; waited < timeoutMs && AnyModifierDown(); waited += ModifierPollMs)
            Sleep(run, ModifierPollMs);
        run.WaitedThisIteration = false;
    }

    bool AnyModifierDown() => _keys.IsDown(VirtualKeys.Control) || _keys.IsDown(VirtualKeys.Shift) || _keys.IsDown(VirtualKeys.Menu)
                              || _keys.IsDown(VirtualKeys.LWin) || _keys.IsDown(VirtualKeys.RWin);

    void Play(Run run, MacroStep step)
    {
        switch (step)
        {
            case KeyTapStep s:
                var modifiers = VirtualKeys.ForModifiers(s.Modifiers);
                foreach (var m in modifiers) Press(run, m);
                Press(run, s.Vk);
                Sleep(run, s.HoldMs);
                Release(run, s.Vk);
                for (int i = modifiers.Count - 1; i >= 0; i--) Release(run, modifiers[i]);
                break;
            case KeyDownStep s:
                Press(run, s.Vk);
                break;
            case KeyUpStep s:
                Release(run, s.Vk);
                break;
            case TypeTextStep s:
                TypeText(run, s.Text ?? "", s.PerCharDelayMs);
                break;
            case DelayStep s:
                Sleep(run, s.Ms);
                break;
            case MouseClickStep s:
                if (s.X is int x && s.Y is int y) _input.SendMouseMove(x, y, relative: false);
                PressButton(run, s.Button);
                ReleaseButton(run, s.Button);
                break;
            case MouseMoveStep s:
                _input.SendMouseMove(s.X, s.Y, s.Relative);
                break;
            case ScrollStep s:
                if (s.Delta != 0) _input.SendMouseWheel(Math.Clamp(s.Delta, -MaxScrollNotches, MaxScrollNotches) * WheelDelta);
                break;
            case LaunchProgramStep s:
                _shell.Launch(s.Path ?? "", s.Arguments, s.WorkingDirectory);
                break;
            case OpenUrlStep s:
                _shell.OpenUrl(s.Url ?? "");
                break;
            case OpenFolderStep s:
                _shell.OpenFolder(s.Path ?? "");
                break;
            case MediaKeyStep s:
                int vk = VirtualKeys.ForMediaKey(s.Key);
                Press(run, vk);
                Release(run, vk);
                break;
            default:
                throw new MacroStepException($"Unsupported step {step.GetType().Name}");
        }
    }

    void TypeText(Run run, string text, int perCharDelayMs)
    {
        bool first = true;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') continue; // CRLF → one Enter
            if (!first && !char.IsLowSurrogate(c)) Sleep(run, perCharDelayMs);
            first = false;
            run.Cts.Token.ThrowIfCancellationRequested();
            switch (c)
            {
                case '\n' or '\r':
                    Press(run, VirtualKeys.Return);
                    Release(run, VirtualKeys.Return);
                    break;
                case '\t':
                    Press(run, VirtualKeys.Tab);
                    Release(run, VirtualKeys.Tab);
                    break;
                default:
                    _input.SendUnicodeChar(c);
                    break;
            }
        }
    }

    void Sleep(Run run, int milliseconds)
    {
        if (milliseconds <= 0) return;
        run.WaitedThisIteration = true;
        if (!_timer.Wait(milliseconds, run.Cts.Token)) throw new OperationCanceledException(run.Cts.Token);
    }

    void Press(Run run, int vk)
    {
        CheckKey(vk);
        _input.SendKeyDown(vk, VirtualKeys.IsExtended(vk));
        if (!run.Keys.Contains(vk)) run.Keys.Add(vk);
    }

    void Release(Run run, int vk)
    {
        CheckKey(vk);
        _input.SendKeyUp(vk, VirtualKeys.IsExtended(vk));
        run.Keys.Remove(vk);
    }

    void PressButton(Run run, MouseButton button)
    {
        _input.SendMouseButton(button, down: true);
        if (!run.Buttons.Contains(button)) run.Buttons.Add(button);
    }

    void ReleaseButton(Run run, MouseButton button)
    {
        _input.SendMouseButton(button, down: false);
        run.Buttons.Remove(button);
    }

    static void CheckKey(int vk)
    {
        if (vk is < 1 or > 254) throw new MacroStepException($"Invalid key code {vk}");
    }

    /// <summary>Balanced release of everything this run still holds, most recent first. Never throws.</summary>
    void ReleaseAll(Run run)
    {
        for (int i = run.Buttons.Count - 1; i >= 0; i--)
        {
            try { _input.SendMouseButton(run.Buttons[i], down: false); }
            catch (Exception e) { Log.Write($"Macro: could not release mouse {run.Buttons[i]}: {e.Message}"); }
        }
        for (int i = run.Keys.Count - 1; i >= 0; i--)
        {
            try { _input.SendKeyUp(run.Keys[i], VirtualKeys.IsExtended(run.Keys[i])); }
            catch (Exception e) { Log.Write($"Macro: could not release key {run.Keys[i]:X2}: {e.Message}"); }
        }
        run.Buttons.Clear();
        run.Keys.Clear();
    }

    static void Raise(Action raise)
    {
        try { raise(); }
        catch (Exception e) { Log.Write($"Macro event handler failed: {e}"); }
    }

    sealed class Run(Macro macro, MacroStep[] steps, long iterations, bool toggle)
    {
        public Macro Macro { get; } = macro;
        public MacroStep[] Steps { get; } = steps;

        /// <summary>Number of plays; negative = until stopped.</summary>
        public long Iterations { get; } = iterations;

        public bool Toggle { get; } = toggle;
        public CancellationTokenSource Cts { get; } = new();
        public List<int> Keys { get; } = [];
        public List<MouseButton> Buttons { get; } = [];
        public bool WaitedThisIteration;
    }
}
