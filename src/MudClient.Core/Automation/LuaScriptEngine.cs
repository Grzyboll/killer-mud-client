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

            _script.DoString(source);

            return commands;
        }
    }
}
