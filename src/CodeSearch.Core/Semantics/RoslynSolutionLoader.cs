using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using System.Diagnostics;
using System.Globalization;
using CodeSearch.Core.Indexing;
using CodeSearch.Core.Resources;
using LocalAi.Contracts;

namespace CodeSearch.Core.Semantics;

public sealed class LoadedRoslynSolution : IAsyncDisposable
{
    private readonly MSBuildWorkspace workspace;
    private readonly Solution solution;
    private readonly RoslynBuildHostLease? buildHostLease;

    internal LoadedRoslynSolution(
        MSBuildWorkspace workspace,
        Solution solution,
        RoslynBuildHostLease? buildHostLease = null,
        IReadOnlyList<string>? uncoveredProjects = null)
    {
        this.workspace = workspace;
        this.solution = solution;
        this.buildHostLease = buildHostLease;
        UncoveredProjects = uncoveredProjects ?? [];
    }

    /// <summary>
    /// Projects in this repository that this load did not cover.
    ///
    /// Only ever non-empty when there is no solution file: a repository with one is opened whole,
    /// but without one a single project is chosen and the rest get no precise navigation at all.
    /// Reported as a list rather than left to a console line, because the caller has to be able
    /// to fail on it -- a repository where nine projects out of ten are missing otherwise reads
    /// exactly like one where none are.
    /// </summary>
    public IReadOnlyList<string> UncoveredProjects { get; }

    /// <param name="log">
    /// Passed through to the analysis, the one step of this phase that can say how far it is.
    /// Optional and before the token, so a caller that does not report keeps its shape.
    /// </param>
    public Task<SemanticIndex> BuildIndexAsync(
        string repositoryRoot,
        SemanticIndexBuildIdentity identity,
        Action<string>? log = null,
        Action<IndexBuildStep, int, int>? step = null,
        CancellationToken cancellationToken = default) =>
        new RoslynSemanticIndexer().BuildAsync(
            solution,
            repositoryRoot,
            identity,
            log,
            step,
            cancellationToken);

    public ValueTask DisposeAsync()
    {
        workspace.Dispose();
        buildHostLease?.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Loads the repository's primary solution using the installed dotnet SDK.</summary>
public static class RoslynSolutionLoader
{
    /// <summary>
    /// How long the dependency restore is allowed to take. Named because the line that
    /// announces the wait quotes it: two copies of ten minutes would drift apart, and the
    /// sentence would then describe a bound that is not the one being waited on.
    /// </summary>
    private static readonly TimeSpan RestoreTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Runs a step that has no counter of its own, saying it is still there while it runs.
    ///
    /// <c>dotnet restore</c> and <c>OpenSolutionAsync</c> are single calls with no per-project
    /// callback — about 21 s and 10 s on the run that produced #387 — so each announced itself,
    /// said what it cost, and said nothing in between. A machine that is genuinely stuck looked
    /// exactly like one twenty seconds into a restore.
    ///
    /// The line carries a time and nothing else. A fraction or an estimate would have to be
    /// invented here, and the cadence is <see cref="IndexBuilder.TargetBatchDuration"/>, so
    /// nothing anywhere in a sync stays silent for longer than one embedding batch.
    ///
    /// A step that finishes inside the interval prints nothing at all, which is the common case
    /// on a warm machine and the whole point: the line's presence is information rather than
    /// noise.
    /// </summary>
    /// <param name="wait">
    /// The pause between lines, injectable so a test can prove the reporting without spending
    /// fifteen real seconds, and prove the silence without pretending to be a fast machine.
    /// </param>
    /// <summary>
    /// A solution or a bare project, whichever the repository has. One method so the step handed
    /// to the heartbeat is a single task rather than a branch, and both shapes are reported the
    /// same way.
    /// </summary>
    private static async Task<Solution> OpenAsync(
        MSBuildWorkspace workspace,
        string selected,
        CancellationToken cancellationToken) =>
        selected.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            ? (await workspace.OpenProjectAsync(
                selected,
                cancellationToken: cancellationToken).ConfigureAwait(false)).Solution
            : await workspace.OpenSolutionAsync(
                selected,
                cancellationToken: cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// One decimal, invariant, as every other duration this product prints — these lines are
    /// quoted verbatim and parsed, so no comma or group separator may appear in any language.
    /// </summary>
    private static string Seconds(Stopwatch elapsed) =>
        elapsed.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture);

    private static readonly object RegistrationLock = new();
    private static readonly IDictionary<string, string> WorkspaceProperties =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Semantic indexing only evaluates the project graph. Vulnerability audit
            // warnings must not become build-stopping errors in repositories that use
            // TreatWarningsAsErrors; production restore/build remains unchanged.
            ["NuGetAudit"] = "false",
        };

    /// <param name="time">Only for the elapsed figure the heartbeat line carries.</param>
    /// <param name="wait">
    /// The pause between heartbeat lines. Both are injectable for the tests and defaulted for
    /// everybody else, so no caller has to know they exist.
    /// </param>
    /// <param name="step">
    /// Which of the two steps in here is running, for the progress record. Neither can be counted
    /// — a restore is a child process with no per-item callback and an open is one call — so what
    /// this buys a reader is the name of the work and, through the heartbeat above, an age that
    /// keeps moving while it happens.
    /// </param>
    public static async Task<LoadedRoslynSolution?> LoadAsync(
        string repositoryRoot,
        Action<string>? diagnostic = null,
        Action<IndexBuildStep, int, int>? step = null,
        TimeProvider? time = null,
        Func<TimeSpan, CancellationToken, Task>? wait = null,
        CancellationToken cancellationToken = default)
    {
        time ??= TimeProvider.System;
        wait ??= Task.Delay;
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var root = Path.GetFullPath(repositoryRoot);
        var candidates = Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path =>
                (path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                 path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)) &&
                !HasBuildOutputSegment(root, path))
            .OrderBy(path => Depth(root, path))
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var projectCandidates = candidates.Length == 0
            ? Directory
                .EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
                .Where(path => !HasBuildOutputSegment(root, path))
                .OrderBy(path => Depth(root, path))
                .ThenBy(path => path, StringComparer.Ordinal)
                .ToArray()
            : [];
        var selected = candidates.FirstOrDefault() ?? projectCandidates.FirstOrDefault();
        if (selected is null)
        {
            return null;
        }

        // Before the wait rather than after it. Everything from here to the first line the sync
        // used to print is one silence — 36.72 s on this repository — and a line explaining it
        // afterwards explains a wait the reader has already sat through (#387).
        diagnostic?.Invoke(IndexText.RoslynLoading(selected, (int)RestoreTimeout.TotalMinutes));
        step?.Invoke(IndexBuildStep.RestoringDependencies, 0, 0);
        var restoring = Stopwatch.StartNew();
        var restored = await QuietStep.WhileItRunsAsync(
            RestoreProjectDependenciesAsync(selected, diagnostic, cancellationToken),
            diagnostic,
            time,
            wait,
            cancellationToken).ConfigureAwait(false);
        if (restored)
        {
            // Only on success. The failure and timeout paths print their own diagnostics, so a
            // run that shows the line above, then a failure, and no line here is telling the
            // truth rather than hiding half of it.
            diagnostic?.Invoke(IndexText.RoslynRestored(Seconds(restoring)));
        }

        using (await RoslynBuildHostLease.AcquireLoadLockAsync(cancellationToken))
        {
            EnsureMsBuildRegistered(diagnostic);
            var buildHostLease = RoslynBuildHostLease.CreateIfNeeded();
            var originalBaseDirectory = AppContext.GetData(
                RoslynBuildHostLease.BaseDirectoryDataName);
            if (buildHostLease is not null)
            {
                AppContext.SetData(
                    RoslynBuildHostLease.BaseDirectoryDataName,
                    buildHostLease.RootPath);
            }

            var workspace = MSBuildWorkspace.Create(WorkspaceProperties);
            // Kind and entry point, not the message alone. A workspace failure's message is
            // whatever exception text Roslyn had at hand, and for a bare DllNotFoundException
            // that is exactly "Dll was not found." — the one anonymous line issue #139 turned
            // on, which five reproduction attempts could not trace because it named neither
            // what was being loaded nor how severe the failure was.
            // One root cause, one line. A repository whose NuGet configuration trips
            // source mapping fails once per project — 53 times on the repository that
            // reported it — and the hostfxr line, or a genuinely different failure,
            // drowns in the repetition (#291).
            var digest = new WorkspaceDiagnosticDigest();
            workspace.RegisterWorkspaceFailedHandler(args =>
            {
                var line = digest.Observe(
                    args.Diagnostic.Kind.ToString(),
                    args.Diagnostic.Message);
                if (line is not null)
                {
                    diagnostic?.Invoke(
                        $"{args.Diagnostic.Kind} while loading '{selected}': {line}");
                }
            });
            try
            {
                step?.Invoke(IndexBuildStep.OpeningProjects, 0, 0);
                var opening = Stopwatch.StartNew();
                var solution = await QuietStep.WhileItRunsAsync(
                    OpenAsync(workspace, selected, cancellationToken),
                    diagnostic,
                    time,
                    wait,
                    cancellationToken).ConfigureAwait(false);
                // After the call, because that is the first moment the count exists: opening a
                // solution is one opaque operation with no per-project callback to count from.
                diagnostic?.Invoke(
                    IndexText.RoslynOpened(solution.Projects.Count(), Seconds(opening)));
                if (candidates.Length + projectCandidates.Length > 1)
                {
                    diagnostic?.Invoke(
                        $"Multiple C# entry points found; semantic indexing selected '{selected}'.");
                }

                // Only the project fallback leaves anything out. A solution is opened whole, so
                // several solutions in one tree means the others were not asked for rather than
                // silently dropped.
                var uncovered = projectCandidates
                    .Where(path => !string.Equals(path, selected, StringComparison.Ordinal))
                    .ToArray();
                return new LoadedRoslynSolution(
                    workspace,
                    solution,
                    buildHostLease,
                    uncovered);
            }
            catch
            {
                workspace.Dispose();
                buildHostLease?.Dispose();
                throw;
            }
            finally
            {
                // After the load rather than as they arrive: the count is only known once
                // every project has been through, and the first of each is already above.
                foreach (var line in digest.Summarise())
                {
                    diagnostic?.Invoke(line);
                }

                AppContext.SetData(
                    RoslynBuildHostLease.BaseDirectoryDataName,
                    originalBaseDirectory);
            }
        }
    }

    private static void EnsureMsBuildRegistered(Action<string>? diagnostic)
    {
        lock (RegistrationLock)
        {
            if (!MSBuildLocator.IsRegistered)
            {
                // Inside the self-contained single-file publish, RegisterDefaults()'s
                // P/Invoke into hostfxr has no module to bind to (#188); the preloader
                // gives it one and changes nothing where the import already resolves.
                HostFxrPreloader.EnsureLoaded(diagnostic);
                MSBuildLocator.RegisterDefaults();
            }
        }
    }

    /// <returns>
    /// <see langword="true"/> when the restore process exited 0. Every other outcome - not
    /// started, timed out, non-zero - has already reported itself through the diagnostic
    /// sink, and the caller uses this only to decide whether to say the restore finished.
    /// </returns>
    private static async Task<bool> RestoreProjectDependenciesAsync(
        string entryPoint,
        Action<string>? diagnostic,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            // The SDK follows the console, and under a Git hook or an MCP server there is no
            // console — so on a Russian Windows it writes code page 866 while this process has
            // set itself to UTF-8. Read as UTF-8, NuGet's explanation of what is wrong with the
            // machine's package sources arrived as mojibake (#292).
            StandardOutputEncoding = ChildProcessText.ConsoleEncoding,
            StandardErrorEncoding = ChildProcessText.ConsoleEncoding,
        };
        foreach (var argument in new[]
                 {
                     "restore",
                     entryPoint,
                     "--nologo",
                     "--verbosity",
                     "quiet",
                     "-p:NuGetAudit=false",
                 })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start())
            {
                diagnostic?.Invoke(
                    $"Project dependency restore did not start for '{entryPoint}'.");
                return false;
            }
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            diagnostic?.Invoke(
                $"Project dependencies were not restored: {exception.Message}");
            return false;
        }

        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var timeout = new CancellationTokenSource(RestoreTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            diagnostic?.Invoke(
                $"Project dependency restore timed out for '{entryPoint}'.");
            return false;
        }

        var output = await stdout.ConfigureAwait(false);
        var error = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(error) ? output : error;
            detail = detail.Trim();
            if (detail.Length > 2_000)
            {
                detail = detail[^2_000..];
            }

            diagnostic?.Invoke(
                $"Project dependency restore failed for '{entryPoint}' " +
                $"with exit code {process.ExitCode}: {detail}");
            return false;
        }

        return true;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }
    }

    private static bool HasBuildOutputSegment(string root, string path) =>
        Path.GetRelativePath(root, path)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment =>
                string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase));

    private static int Depth(string root, string path) =>
        Path.GetRelativePath(root, path)
            .Count(character =>
                character == Path.DirectorySeparatorChar ||
                character == Path.AltDirectorySeparatorChar);
}
