using System.ComponentModel;
using System.Diagnostics;
using CodeSearch.Core.Semantics;

// A separate host gives each regression its own process CWD and environment.
if (args is ["mark", var markerPath])
{
    File.WriteAllText(markerPath, "harmless semantic-tool fixture");
    return 23;
}

if (args is not ["probe", var launcher, var executable, var workspace, var marker])
{
    Console.Error.WriteLine("Expected probe <scip|lsp> <executable> <workspace> <marker>.");
    return 2;
}

// This regression isolates implicit OS lookup, not an intentionally configured PATH entry.
if (!Path.IsPathRooted(executable) && ExecutableResolver.Find(executable) is not null)
{
    Console.WriteLine("ConfiguredLookup");
    return 0;
}

if (launcher == "scip")
{
    var index = new SemanticIndex
    {
        RepositoryId = "fixture", GenerationId = "fixture", GitTree = "fixture", BaseCommit = "fixture",
        IndexedAtUtc = DateTime.UnixEpoch, Documents = [], Symbols = [], Occurrences = [], Relationships = []
    };
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var result = await new ScipAdapterRunner().RunAsync(index, workspace,
        new ScipAdapterSpec("fixture", executable, ["mark", marker]), timeout.Token);
    Console.WriteLine(result.Status.State);
    return 0;
}

if (launcher == "lsp")
{
    try
    {
        await using var client = StdioLanguageServerClient.Start(workspace,
            new LanguageServerProcessSpec(executable, ["mark", marker]));
        // Give the harmless marker time to exit before disposing the uninitialized client.
        var deadline = Stopwatch.StartNew();
        while (!File.Exists(marker) && deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(20);
        }

        if (!File.Exists(marker))
        {
            throw new TimeoutException("The selected marker executable did not finish its write.");
        }

        Console.WriteLine("Started");
    }
    catch (InvalidOperationException exception) when (
        exception.InnerException is Win32Exception { NativeErrorCode: 2 or 3 })
    {
        Console.WriteLine("Missing");
    }

    return 0;
}

return 2;
