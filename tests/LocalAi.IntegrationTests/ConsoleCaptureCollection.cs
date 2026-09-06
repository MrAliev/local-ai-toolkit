namespace LocalAi.IntegrationTests;

/// <summary>
/// The collection every test that reads what a command printed belongs to.
///
/// `Console.Error` is process-global, and reading a command's output means replacing it for
/// the duration of the call. xUnit runs test classes in parallel, so two classes doing that
/// at once swap each other's writers: one assertion sees an empty string and the other sees
/// the first one's text. It is a race, so it hides for a long time and then reproduces
/// every run once the timing settles — which is how it was found, after a change that
/// touched neither test.
///
/// Tests inside one collection never run concurrently, which is the whole fix. The
/// alternative — passing a writer into each command instead of reaching for the console —
/// is the better design and a larger change; this makes the suite honest today.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ConsoleCaptureCollection
{
    public const string Name = "console capture";
}
