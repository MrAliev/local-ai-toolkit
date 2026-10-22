namespace LocalAi.Contracts;

/// <summary>
/// How a Windows <c>.cmd</c> or <c>.bat</c> is launched, in one place.
///
/// Windows cannot start a command script directly: it has to be handed to the interpreter, and
/// the interpreter takes one command line rather than an argument list. Building that line is
/// the whole risk, because everything inside it is subject to the interpreter's own expansion.
///
/// The policy: every value goes in quotes, and a value carrying <c>"</c>, <c>%</c>, <c>!</c> or a
/// line break is refused rather than escaped. The quote would end the quoting; <c>%</c> and
/// <c>!</c> are the substitution vectors, the second of them only when delayed expansion is on,
/// which this code cannot see from here; a line break would start a second command. Refusing is
/// the answer rather than escaping because the escaping rules differ between the two expansions
/// and there is no path here that needs those characters.
///
/// This lived in three copies — <c>ScipAdapterRunner</c>, <c>StdioLanguageServerClient</c> and
/// <c>SystemProcessRunner</c> — identical down to the rejection list, with nothing tying them
/// together, so an edit to one would not have reached the other two (#401). It is one decision
/// and it is made once.
/// </summary>
public static class WindowsCommandScript
{
    /// <summary>
    /// Whether this executable has to go through the interpreter. False off Windows, where a
    /// <c>.cmd</c> is not special and nothing expands.
    /// </summary>
    public static bool Is(string executable)
    {
        ArgumentNullException.ThrowIfNull(executable);
        return OperatingSystem.IsWindows() &&
            (executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
             executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The fully qualified interpreter named by <c>ComSpec</c>, or the system
    /// <c>cmd.exe</c> when the setting is missing, blank, bare, or relative.
    /// </summary>
    public static string Interpreter
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("ComSpec");
            return !string.IsNullOrWhiteSpace(configured) && Path.IsPathFullyQualified(configured)
                ? configured
                : Path.Combine(Environment.SystemDirectory, "cmd.exe");
        }
    }

    /// <param name="refusal">
    /// The message of the <see cref="ArgumentException"/> when a value is unsafe. Passed in
    /// rather than written here because each caller already had its own sentence naming what it
    /// was launching — an adapter, a language server, a child process — and this change is about
    /// the policy being in one place, not about rewording what a reader is told.
    /// </param>
    /// <returns>
    /// The complete <c>Arguments</c> value for the interpreter: <c>/d /s /c</c> and the quoted
    /// command line. Read from <c>cmd /?</c> rather than remembered — <c>/d</c> disables the
    /// execution of AutoRun commands from the registry, so a machine carrying that hook does not
    /// get to run something first; <c>/s</c> modifies how the string after <c>/c</c> is treated,
    /// which is what makes the outer quotes here behave; <c>/c</c> carries out the command and
    /// then terminates.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// A value carries a character the command line cannot safely hold.
    /// </exception>
    public static string Arguments(
        string executable,
        IReadOnlyList<string> arguments,
        string refusal)
    {
        ArgumentNullException.ThrowIfNull(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(refusal);
        var values = new[] { executable }.Concat(arguments).ToArray();
        if (values.Any(value => value.IndexOfAny(['"', '\r', '\n', '%', '!']) >= 0))
        {
            throw new ArgumentException(refusal);
        }

        return "/d /s /c \"" +
            string.Join(" ", values.Select(value => $"\"{value}\"")) +
            "\"";
    }
}
