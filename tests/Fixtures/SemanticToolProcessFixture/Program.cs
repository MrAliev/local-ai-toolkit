using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using CodeSearch.Core.Indexing;
using CodeSearch.Core.Semantics;
using LocalAi.Contracts;

// A separate host gives each regression its own process CWD and environment.
if (args is ["probe-runtime-root", var expectedUserData])
{
    var runtimeRoot = ModelResidencyPolicyStore.DefaultRuntimeRoot;
    var expectedRoot = Path.Combine(expectedUserData, "LocalAi");
    if (!Path.IsPathFullyQualified(runtimeRoot) || runtimeRoot != expectedRoot)
    {
        throw new InvalidOperationException("The default runtime root did not preserve the chosen user-data location.");
    }

    var dataExistsAfterLookup = Directory.Exists(expectedUserData);
    var store = new ModelResidencyPolicyStore(runtimeRoot);
    store.Write(BrokerPolicy.Default);
    Console.Write(JsonSerializer.Serialize(new
    {
        RuntimeRoot = runtimeRoot,
        DataExistsAfterLookup = dataExistsAfterLookup,
        SettingsFile = RuntimeDirectories.SettingsFileForWriting(runtimeRoot, BrokerPolicy.FileName),
        RoundTrip = store.Read() == BrokerPolicy.Default,
    }));
    return 0;
}

if (args is ["git-fsmonitor", var gitMarker, ..])
{
    WriteMarker(gitMarker);
    Console.Write("fixture-token\0");
    return 0;
}

if (args is ["probe-snapshot", var repository, var revision, var relativeDirectory, var gitDirectory])
{
    using var snapshot = CommitSnapshot.Create(repository, revision);
    if (gitDirectory != "-")
    {
        // An explicit repository override is a separate, supported host authority.
        Environment.SetEnvironmentVariable("GIT_DIR", gitDirectory);
    }

    var directory = Path.Combine(snapshot.Root, relativeDirectory);
    var files = FileScanner.Enumerate(snapshot.Root);
    var listed = RepoLocator.GitOutputBytes(directory,
        ["ls-files", "--cached", "--others", "--exclude-standard", "-z"]);
    var diff = RepoLocator.GitOutputBytes(directory,
        ["diff", "--no-renames", "--name-only", "-z", "HEAD", "--"]);
    Console.Write(JsonSerializer.Serialize(new
    {
        Files = files.Select(path => path.Replace('\\', '/')).ToArray(),
        Content = File.ReadAllText(Path.Combine(snapshot.Root, "Root.cs")),
        Commit = RepoLocator.GitCommit(directory),
        CommonDirectory = RepoLocator.GitOutput(directory, "rev-parse --path-format=absolute --git-common-dir"),
        Listed = listed is not null,
        Diffed = diff is not null,
        SnapshotRoot = snapshot.Root,
    }));
    return 0;
}

if (args is ["mark", var markerPath])
{
    WriteMarker(markerPath);
    return 23;
}

if (string.Equals(Path.GetFileName(Environment.ProcessPath), "cmd.exe", StringComparison.OrdinalIgnoreCase))
{
    // The isolated interpreter probe copies this fixture beside its marker destination.
    WriteMarker(Path.Combine(AppContext.BaseDirectory, "interpreter-selected.txt"));
    return 23;
}

string launcher, executable, workspace, marker;
string? completionPath = null;
var interpreterProbe = false;
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
else if (args is ["probe-interpreter", var interpreterLauncher, var interpreterExecutable,
             var interpreterWorkspace, var interpreterMarker])
{
    (launcher, executable, workspace, marker) =
        (interpreterLauncher, interpreterExecutable, interpreterWorkspace, interpreterMarker);
    completionPath = marker + ".done";
    interpreterProbe = true;
}
else
{
    Console.Error.WriteLine("Expected probe/probe-interpreter <scip|lsp> <executable> <workspace> <marker>, or probe-script with scenario and dependency.");
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
        bool HasCompleted() => File.Exists(completed) || (interpreterProbe && File.Exists(marker));
        var deadline = Stopwatch.StartNew();
        while (!HasCompleted() && deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(20);
        }

        if (!HasCompleted())
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

static void WriteMarker(string path)
{
    File.WriteAllText(path + ".writing", Environment.ProcessPath ?? "unknown");
    File.Move(path + ".writing", path);
}
