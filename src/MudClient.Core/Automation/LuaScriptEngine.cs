using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using MoonSharp.Interpreter;

namespace MudClient.Core.Automation;

/// <summary>Live game state exposed to Lua scripts as plain globals — see
/// <see cref="LuaScriptEngine.Run"/>.</summary>
public sealed record LuaGameState(
    int? Hp,
    int? MaxHp,
    int? Mv,
    int? MaxMv,
    string? CharacterName,
    string? Position,
    string? RoomVnum,
    string? RoomName,
    IReadOnlyList<string>? SkillsOnCooldown = null);

/// <summary>
/// Shared Lua environment for "script" aliases/triggers/timers (see
/// <see cref="AliasRule.IsScript"/>/<see cref="TriggerRule.IsScript"/>). One instance lives for
/// the whole app session, so plain Lua globals a script assigns (e.g. <c>count = (count or 0) +
/// 1</c>) persist across every later firing — the same single-shared-VM model well-known MUD
/// clients like Mudlet use, rather than a fresh sandbox per rule. The host calls
/// <see cref="Reset"/> when the active profile changes and then <see cref="LoadLibrary"/> with
/// that profile's own helper-function source, so state never leaks between characters despite
/// the engine instance itself being long-lived. MoonSharp's <see cref="Script"/> is not safe for
/// concurrent use, so every <see cref="Run"/> call is serialized under a lock: aliases fire from
/// the UI thread, triggers from the network read loop, and timers from their own callback
/// thread, all sharing this one engine.
/// </summary>
public sealed class LuaScriptEngine
{
    /// <summary>Unlike e.g. Jint (LimitMemory/MaxStatements/TimeoutInterval/LimitRecursion),
    /// MoonSharp 2.0.0 has no built-in execution limit, and .NET provides no safe way to forcibly
    /// stop a running managed thread (Thread.Abort was removed after .NET Framework) — so a
    /// runaway script (e.g. "while true do end", or one imported from someone else's shared
    /// triggers/aliases) can only be given up on, not stopped. This bounds how long any one
    /// <see cref="Run"/> call waits before doing that — see <see cref="ExecuteWithTimeout"/>.</summary>
    private static readonly TimeSpan ExecutionTimeout = TimeSpan.FromSeconds(2);

    private Script _script = new();
    private readonly object _lock = new();

    /// <summary>Supplies a fresh game-state snapshot before every run — set once by the host
    /// (MainWindowViewModel). Null values inside the returned record become Lua <c>nil</c>.</summary>
    public Func<LuaGameState>? GameStateProvider { get; set; }

    /// <summary>Raised for every <c>echo(text)</c> call a script makes, so the host can print it
    /// to the terminal the same way a triggered command's own echo works.</summary>
    public event Action<string>? Echo;

    public LuaScriptEngine()
    {
        RegisterEcho();
    }

    private void RegisterEcho() =>
        _script.Globals["echo"] = (Action<string>)(text => Echo?.Invoke(text ?? string.Empty));

    /// <summary>
    /// Throws away every global a script has ever set (persistent counters, library-defined
    /// helper functions, everything) and starts a brand new environment — called when the active
    /// profile changes, so one character's automation state can never leak into another's.
    /// <see cref="GameStateProvider"/> and <see cref="Echo"/> subscribers are untouched; only the
    /// Lua-side state resets.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _script = new Script();
            RegisterEcho();
        }
    }

    /// <summary>
    /// Executes <paramref name="source"/> once, for its side effects on the shared global
    /// environment — meant for defining reusable helper functions/values (see
    /// <c>ProfileData.LuaLibrary</c>) that every later alias/trigger/timer script can then call.
    /// Internally just <see cref="Run"/> with no line/match context, discarding any <c>send</c>
    /// calls the source happens to make (library code isn't meant to send commands directly).
    /// A blank/whitespace-only source is a silent no-op.
    /// </summary>
    /// <exception cref="InterpreterException">The library has a syntax error, or threw at
    /// runtime.</exception>
    public void LoadLibrary(string? source)
    {
        if (!string.IsNullOrWhiteSpace(source))
        {
            Run(source, line: null, match: null);
        }
    }

    /// <summary>
    /// Runs <paramref name="source"/> in the shared global environment and returns every command
    /// the script asked to send via <c>send("...")</c>, in call order (empty if it called
    /// <c>send</c> zero times — that's a normal, silent outcome, not an error).
    /// </summary>
    /// <param name="line">The raw matched MUD line (trigger) or raw typed command (alias),
    /// exposed as the Lua global <c>line</c>. Null for a timer, which has neither.</param>
    /// <param name="match">The regex match that fired this rule, if any. Its whole-match text
    /// becomes the Lua global <c>match</c>; its numbered capture groups become the 1-indexed Lua
    /// table <c>matches</c> (<c>matches[1]</c> lines up with the old text-template system's
    /// <c>$1</c>). Null for a timer, which has no pattern to match.</param>
    /// <exception cref="InterpreterException">The script has a syntax error, or threw at
    /// runtime — callers know which rule this was, so they catch and report it, not this engine.</exception>
    public IReadOnlyList<string> Run(string source, string? line, Match? match)
    {
        lock (_lock)
        {
            var commands = new List<string>();
            _script.Globals["send"] = (Action<string>)(command =>
            {
                if (!string.IsNullOrWhiteSpace(command))
                {
                    commands.Add(command);
                }
            });

            _script.Globals["line"] = line;
            _script.Globals["match"] = match is { Success: true } ? match.Value : null;

            var matches = new Table(_script);
            if (match is { Success: true })
            {
                for (var i = 1; i < match.Groups.Count; i++)
                {
                    matches[i] = match.Groups[i].Success ? match.Groups[i].Value : string.Empty;
                }
            }

            _script.Globals["matches"] = matches;

            var state = GameStateProvider?.Invoke();
            _script.Globals["hp"] = state?.Hp;
            _script.Globals["maxhp"] = state?.MaxHp;
            _script.Globals["mv"] = state?.Mv;
            _script.Globals["maxmv"] = state?.MaxMv;
            _script.Globals["charname"] = state?.CharacterName;
            _script.Globals["position"] = state?.Position;
            _script.Globals["roomvnum"] = state?.RoomVnum;
            _script.Globals["roomname"] = state?.RoomName;

            // Lookup table keyed by lower-cased skill name (matches how this MUD's Char.Skills
            // .Timeout GMCP names skills) — skills_on_cooldown["smite evil"] is true while the
            // skill can't be used, and nil/false once it's usable again (or was never learned).
            var skillsOnCooldown = new Table(_script);
            if (state?.SkillsOnCooldown is { } cooldowns)
            {
                foreach (var name in cooldowns)
                {
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        skillsOnCooldown[name.Trim().ToLowerInvariant()] = true;
                    }
                }
            }

            _script.Globals["skills_on_cooldown"] = skillsOnCooldown;

            ExecuteWithTimeout(source);

            return commands;
        }
    }

    /// <summary>Runs <see cref="Script.DoString"/> on a dedicated background thread and waits up
    /// to <see cref="ExecutionTimeout"/> for it. A script that never returns can't be forcibly
    /// stopped, so instead of hanging every later <see cref="Run"/> call behind it forever, this
    /// abandons the stuck thread (it keeps one core busy until the process exits — the best
    /// available outcome without Thread.Abort) and swaps in a brand new <see cref="Script"/> for
    /// everything from now on, the same reset <see cref="Reset"/> does for a profile switch, then
    /// reports the timeout as an ordinary Lua runtime error so existing call sites (which already
    /// catch <see cref="InterpreterException"/> for a syntax/runtime error) handle it unchanged.
    /// Must only be called while already holding <see cref="_lock"/>.</summary>
    /// <exception cref="InterpreterException">The script had a syntax error, threw at runtime, or
    /// exceeded <see cref="ExecutionTimeout"/>.</exception>
    private void ExecuteWithTimeout(string source)
    {
        ExceptionDispatchInfo? capturedException = null;
        var thread = new Thread(() =>
        {
            try
            {
                _script.DoString(source);
            }
            catch (Exception exception)
            {
                capturedException = ExceptionDispatchInfo.Capture(exception);
            }
        })
        {
            IsBackground = true,
            Name = "LuaScriptEngine.Run",
        };
        thread.Start();

        if (!thread.Join(ExecutionTimeout))
        {
            _script = new Script();
            RegisterEcho();
            throw new ScriptRuntimeException(
                $"Skrypt Lua przekroczył limit czasu wykonania ({ExecutionTimeout.TotalSeconds:0}s) i został przerwany.");
        }

        capturedException?.Throw();
    }
}
