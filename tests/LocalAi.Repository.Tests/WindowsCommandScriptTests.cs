using LocalAi.Contracts;

namespace LocalAi.Repository.Tests;

/// <summary>
/// The launch policy for Windows command scripts, tested where it now lives rather than at three
/// call sites that each held a copy of it (#401).
///
/// These are characterisation tests: the behaviour is what the three copies already did, and
/// <c>SystemProcessRunnerTests</c> still covers it end to end through a real <c>.cmd</c>. What
/// they add is that the policy has one description, so a future edit to the rejection list is
/// visibly an edit to a rule rather than to one caller.
///
/// <c>Interpreter</c> has no test of its own. It reads one environment variable and falls back to
/// a literal, so a test either restates that line or sets <c>ComSpec</c> for the whole process —
/// which is the shared-state trap #405 was about, for no evidence in return.
/// </summary>
public sealed class WindowsCommandScriptTests
{
    /// <summary>
    /// Every one of these ends the quoting or starts something of its own inside a command line:
    /// a quote closes the value, <c>%</c> and <c>!</c> are the two expansions, and a line break
    /// begins a second command. They are refused rather than escaped because the escaping differs
    /// between the two expansions and no path here needs them.
    /// </summary>
    [Theory]
    [InlineData("\"")]
    [InlineData("%")]
    [InlineData("!")]
    [InlineData("\r")]
    [InlineData("\n")]
    public void A_value_the_command_line_cannot_hold_is_refused(string unsafeCharacter)
    {
        var inTheArgument = Record.Exception(() => WindowsCommandScript.Arguments(
            @"C:\Tools\npm.cmd",
            ["run", "build" + unsafeCharacter],
            "refused"));
        var inTheExecutable = Record.Exception(() => WindowsCommandScript.Arguments(
            @"C:\Tools\np" + unsafeCharacter + "m.cmd",
            ["run"],
            "refused"));

        // The executable is checked with the arguments, not trusted for being a path: it reaches
        // the same command line and expands the same way.
        Assert.IsType<ArgumentException>(inTheArgument);
        Assert.IsType<ArgumentException>(inTheExecutable);
    }

    /// <summary>
    /// The message belongs to the caller. Each of the three sites already had a sentence naming
    /// what it was launching, and moving the policy was not an occasion to reword them.
    /// </summary>
    [Fact]
    public void The_refusal_says_what_the_caller_gave_it()
    {
        var error = Assert.Throws<ArgumentException>(() => WindowsCommandScript.Arguments(
            @"C:\Tools\npm.cmd",
            ["%PATH%"],
            "Windows language-server command paths and arguments contain unsafe characters."));

        Assert.StartsWith(
            "Windows language-server command paths and arguments contain unsafe characters.",
            error.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Every value is quoted, not only the ones that look like they need it: a space in a path is
    /// the ordinary case here — <c>C:\Program Files</c> — and deciding per value which to quote is
    /// how the odd one gets missed.
    /// </summary>
    [Fact]
    public void A_safe_command_is_quoted_whole_and_handed_to_the_interpreter()
    {
        var arguments = WindowsCommandScript.Arguments(
            @"C:\Program Files\nodejs\npm.cmd",
            ["run", "build", "--", "src/app"],
            "refused");

        Assert.Equal(
            "/d /s /c \"\"C:\\Program Files\\nodejs\\npm.cmd\" \"run\" \"build\" \"--\" \"src/app\"\"",
            arguments);
    }

    [Fact]
    public void An_empty_argument_list_still_produces_a_command()
    {
        var arguments = WindowsCommandScript.Arguments(@"C:\Tools\build.bat", [], "refused");

        Assert.Equal("/d /s /c \"\"C:\\Tools\\build.bat\"\"", arguments);
    }

    [Theory]
    [InlineData(@"C:\Tools\npm.cmd", true)]
    [InlineData(@"C:\Tools\build.BAT", true)]
    [InlineData(@"C:\Tools\npm.CMD", true)]
    [InlineData(@"C:\Tools\node.exe", false)]
    [InlineData("npm", false)]
    // A path that merely contains the extension is not one that ends with it.
    [InlineData(@"C:\cmd.bat.d\tool", false)]
    public void What_has_to_go_through_the_interpreter_is_decided_by_the_extension(
        string executable,
        bool expected)
    {
        if (!OperatingSystem.IsWindows())
        {
            // Off Windows a .cmd is an ordinary file and nothing expands, so the answer is always
            // false and this table says nothing.
            Assert.False(WindowsCommandScript.Is(executable));
            return;
        }

        Assert.Equal(expected, WindowsCommandScript.Is(executable));
    }
}
