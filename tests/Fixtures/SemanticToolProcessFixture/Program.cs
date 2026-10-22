using System.ComponentModel;
using System.Diagnostics;
using CodeSearch.Core.Semantics;

// A separate host gives each regression its own process CWD and environment.
if (args is ["mark", var markerPath])
{
    File.WriteAllText(markerPath, Environment.ProcessPath ?? "unknown");
    return 23;
}

string launcher, executable, workspace, marker;
string? completionPath = null;
if (args is ["probe", var nativeLauncher, var nativeExecutable, var nativeWorkspace, var nativeMarker])
{
    (launcher, executable, workspace, marker) = (nativeLauncher, nativeExecutable, nativeWorkspace, nativeMarker);
}
else if (args is ["probe-script", var scriptLauncher, var scriptExecutable, var scriptWorkspace,
             var scriptMarker, var scenario, var dependency])
{
    (launcher, executable, workspace, marker) = (scriptLauncher, scriptExecutable, scriptWorkspace, scriptMarker);
    completionPath = marker + ".done";
    // A deliberate PATH entry resolving inside the workspace is outside this regression.
    var configuredDependency = ExecutableResolver.Find(dependency);
    if (scenario != "explicit-relative" && configuredDependency is not null &&
        string.Equals(Path.GetDirectoryName(configuredDependency), workspace, StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("ConfiguredLookup");
        return 0;
    }
}
else
{
    Console.Error.WriteLine("Expected probe <scip|lsp> <executable> <workspace> <marker>, or probe-script with scenario and dependency.");
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
        // Script completion also covers the case where dependency lookup was blocked.
        var completed = completionPath ?? marker;
        var deadline = Stopwatch.StartNew();
        while (!File.Exists(completed) && deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(20);
        }

        if (!File.Exists(completed))
        {
            throw new TimeoutException("The selected fixture did not finish its write.");
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
