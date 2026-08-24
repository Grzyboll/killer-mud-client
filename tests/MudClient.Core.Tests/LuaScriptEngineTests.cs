using System.Text.RegularExpressions;
using MoonSharp.Interpreter;
using MudClient.Core.Automation;

namespace MudClient.Core.Tests;

public sealed class LuaScriptEngineTests
{
    [Fact]
    public void Run_NoSendCalls_ReturnsEmpty()
    {
        var engine = new LuaScriptEngine();

        var result = engine.Run("local x = 1", line: null, match: null);

        Assert.Empty(result);
    }

    [Fact]
    public void Run_SingleSendCall_ReturnsOneCommand()
    {
        var engine = new LuaScriptEngine();

        var result = engine.Run("send(\"look\")", line: null, match: null);

        Assert.Equal(["look"], result);
    }

    [Fact]
    public void Run_MultipleSendCalls_ReturnsInOrder()
    {
        var engine = new LuaScriptEngine();

        var result = engine.Run("send(\"north\") send(\"look\") send(\"south\")", line: null, match: null);

        Assert.Equal(["north", "look", "south"], result);
    }

    [Fact]
    public void Run_SendBlankOrWhitespace_IsSkipped()
    {
        var engine = new LuaScriptEngine();

        var result = engine.Run("send(\"look\") send(\"\") send(\"   \")", line: null, match: null);

        Assert.Equal(["look"], result);
    }

    [Fact]
    public void Run_ExposesLineGlobal()
    {
        var engine = new LuaScriptEngine();

        var result = engine.Run("send(line)", "Zabijasz golema.", match: null);

        Assert.Equal(["Zabijasz golema."], result);
    }

    [Fact]
    public void Run_ExposesMatchAndCaptureGroups()
    {
        var engine = new LuaScriptEngine();
        var match = Regex.Match("Zabijasz golema.", @"^Zabijasz (.+)\.$");

        var result = engine.Run("send(match) send(matches[1])", "Zabijasz golema.", match);

        Assert.Equal(["Zabijasz golema.", "golema"], result);
    }

    [Fact]
    public void Run_NoMatch_MatchesTableIsEmpty()
    {
        var engine = new LuaScriptEngine();

        var result = engine.Run("send(tostring(#matches))", line: null, match: null);

        Assert.Equal(["0"], result);
    }

    [Fact]
    public void Run_GlobalsPersistAcrossCalls()
    {
        var engine = new LuaScriptEngine();

        engine.Run("count = (count or 0) + 1", line: null, match: null);
        engine.Run("count = (count or 0) + 1", line: null, match: null);
        var result = engine.Run("send(tostring(count))", line: null, match: null);

        Assert.Equal(["2"], result);
    }

    [Fact]
    public void Run_EchoCall_RaisesEchoEvent()
    {
        var engine = new LuaScriptEngine();
        var echoed = new List<string>();
        engine.Echo += echoed.Add;

        engine.Run("echo(\"witam\")", line: null, match: null);

        Assert.Equal(["witam"], echoed);
    }

    [Fact]
    public void Run_GameStateProvider_ExposesValuesAsGlobals()
    {
        var engine = new LuaScriptEngine
        {
            GameStateProvider = () => new LuaGameState(50, 100, 20, 40, "Frodo", "fighting", "6017", "Ciemny las"),
        };

        var result = engine.Run(
            "send(tostring(hp)) send(tostring(maxhp)) send(tostring(mv)) send(tostring(maxmv)) " +
            "send(charname) send(position) send(roomvnum) send(roomname)",
            line: null, match: null);

        Assert.Equal(["50", "100", "20", "40", "Frodo", "fighting", "6017", "Ciemny las"], result);
    }

    [Fact]
    public void Run_NoGameStateProvider_GlobalsAreNil()
    {
        var engine = new LuaScriptEngine();

        var result = engine.Run("send(tostring(hp))", line: null, match: null);

        Assert.Equal(["nil"], result);
    }

    [Fact]
    public void Run_SkillsOnCooldown_LookupTableReflectsCooldownState()
    {
        var engine = new LuaScriptEngine
        {
            GameStateProvider = () => new LuaGameState(
                50, 100, 20, 40, "Frodo", "fighting", "6017", "Ciemny las",
                SkillsOnCooldown: ["smite evil", "holy prayer"]),
        };

        var result = engine.Run(
            "send(tostring(skills_on_cooldown[\"smite evil\"])) " +
            "send(tostring(skills_on_cooldown[\"kick\"]))",
            line: null, match: null);

        Assert.Equal(["true", "nil"], result);
    }

    [Fact]
    public void Run_SkillsOnCooldown_LookupIsCaseInsensitiveViaLowercasing()
    {
        var engine = new LuaScriptEngine
        {
            GameStateProvider = () => new LuaGameState(
                null, null, null, null, null, null, null, null,
                SkillsOnCooldown: ["Smite Evil"]),
        };

        var result = engine.Run("send(tostring(skills_on_cooldown[\"smite evil\"]))", line: null, match: null);

        Assert.Equal(["true"], result);
    }

    [Fact]
    public void Run_NoGameStateProvider_SkillsOnCooldownIsEmptyTableNotNil()
    {
        var engine = new LuaScriptEngine();

        var result = engine.Run(
            "send(tostring(skills_on_cooldown ~= nil)) send(tostring(skills_on_cooldown[\"x\"]))",
            line: null, match: null);

        Assert.Equal(["true", "nil"], result);
    }

    [Fact]
    public void Run_SyntaxError_ThrowsInterpreterException()
    {
        var engine = new LuaScriptEngine();

        Assert.Throws<SyntaxErrorException>(() => engine.Run("this is not lua {{{", line: null, match: null));
    }

    [Fact]
    public void Run_RuntimeError_ThrowsInterpreterException()
    {
        var engine = new LuaScriptEngine();

        Assert.Throws<ScriptRuntimeException>(() => engine.Run("error(\"boom\")", line: null, match: null));
    }

    [Fact]
    public void Run_FailedScript_DoesNotLeakPartialSendsFromThatCall()
    {
        var engine = new LuaScriptEngine();

        Assert.ThrowsAny<InterpreterException>(() =>
            engine.Run("send(\"one\") error(\"boom\")", line: null, match: null));

        // The engine itself is still usable afterward — a failed run doesn't corrupt shared state.
        var result = engine.Run("send(\"two\")", line: null, match: null);
        Assert.Equal(["two"], result);
    }

    // ====================================================================
    // Reset / LoadLibrary — per-profile shared helper functions
    // ====================================================================

    [Fact]
    public void LoadLibrary_DefinesAFunctionOtherRunCallsCanUse()
    {
        var engine = new LuaScriptEngine();

        engine.LoadLibrary("function shout(name) return \"OGŁASZA: \" .. name end");
        var result = engine.Run("send(shout(\"smok\"))", line: null, match: null);

        Assert.Equal(["OGŁASZA: smok"], result);
    }

    [Fact]
    public void LoadLibrary_BlankSource_IsANoOp()
    {
        var engine = new LuaScriptEngine();

        engine.LoadLibrary(null);
        engine.LoadLibrary("");
        engine.LoadLibrary("   ");

        // Nothing thrown, and no leftover state from an empty/whitespace "run".
        var result = engine.Run("send(\"ok\")", line: null, match: null);
        Assert.Equal(["ok"], result);
    }

    [Fact]
    public void LoadLibrary_SendCallsInsideLibrarySourceAreDiscarded()
    {
        var engine = new LuaScriptEngine();

        engine.LoadLibrary("send(\"should not leak anywhere\")");
        var result = engine.Run("send(\"real\")", line: null, match: null);

        Assert.Equal(["real"], result);
    }

    [Fact]
    public void LoadLibrary_SyntaxError_Throws()
    {
        var engine = new LuaScriptEngine();

        Assert.Throws<SyntaxErrorException>(() => engine.LoadLibrary("function broken("));
    }

    [Fact]
    public void Reset_ClearsPersistentGlobals()
    {
        var engine = new LuaScriptEngine();
        engine.Run("count = 5", line: null, match: null);

        engine.Reset();

        var result = engine.Run("send(tostring(count))", line: null, match: null);
        Assert.Equal(["nil"], result);
    }

    [Fact]
    public void Reset_ClearsLibraryDefinedFunctions()
    {
        var engine = new LuaScriptEngine();
        engine.LoadLibrary("function helper() return 1 end");

        engine.Reset();

        Assert.Throws<ScriptRuntimeException>(() => engine.Run("send(tostring(helper()))", line: null, match: null));
    }

    [Fact]
    public void Reset_PreservesGameStateProviderAndEchoSubscription()
    {
        var engine = new LuaScriptEngine
        {
            GameStateProvider = () => new LuaGameState(1, 2, 3, 4, "Frodo", "standing", "1", "Shire"),
        };
        var echoed = new List<string>();
        engine.Echo += echoed.Add;

        engine.Reset();
        engine.Run("echo(charname)", line: null, match: null);

        Assert.Equal(["Frodo"], echoed);
    }
}
