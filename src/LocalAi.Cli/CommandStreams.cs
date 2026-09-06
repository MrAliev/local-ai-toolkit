namespace LocalAi.Cli;

/// <summary>
/// Where a command's output goes, passed in rather than reached for.
///
/// <c>Console.Out</c> and <c>Console.Error</c> belong to the process, so a test that replaces one
/// to read what a command printed replaces it for every test running beside it. Two classes in
/// <c>LocalAi.IntegrationTests</c> did exactly that and swapped each other's writers: one
/// assertion saw an empty string and the other saw the first one's text (#405). It was a race, so
/// it stayed quiet for months and then reproduced every run once the timing settled, after a
/// change that touched neither test.
///
/// Taken as a required parameter rather than one that defaults to the console, because the patch
/// this replaces — putting both classes in one xUnit collection — worked only while everybody
/// remembered to add the attribute. A parameter nobody can leave out cannot be forgotten.
/// </summary>
internal readonly record struct CommandStreams(TextWriter Out, TextWriter Error)
{
    /// <summary>
    /// The process's own streams, read at the moment of the call rather than once at startup:
    /// <c>Console.SetOut</c> during a run must still be visible, which is how the launcher and
    /// the tests that check encoding behave.
    /// </summary>
    public static CommandStreams Standard => new(Console.Out, Console.Error);
}
