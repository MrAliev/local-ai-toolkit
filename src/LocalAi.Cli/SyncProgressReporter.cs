using System.Globalization;
using LocalAi.Contracts;
using LocalAi.Contracts.Localization;

namespace LocalAi.Cli;

/// <summary>Reports every phase, including work without item counters, until disposal.</summary>
internal sealed class SyncProgressReporter : IDisposable
{
    internal static readonly TextCatalogue Catalogue = new("LocalAi.Cli.Resources.SyncProgress", typeof(SyncProgressReporter).Assembly);
    private readonly object _gate = new();
    private readonly Action<RepositoryIndexProgress> _save;
    private readonly TextWriter _output;
    private readonly TimeProvider _time;
    private readonly ITimer _timer;
    private readonly long _started;
    private RepositoryIndexProgress? _latest;
    private bool _disposed;
    private readonly Func<string>? _operation;

    public SyncProgressReporter(Action<RepositoryIndexProgress> save, TextWriter output, TimeProvider? time = null,
        Func<string>? operation = null)
    {
        _save = save;
        _output = output;
        _time = time ?? TimeProvider.System;
        _started = _time.GetTimestamp();
        _operation = operation;
        _timer = _time.CreateTimer(_ => Tick(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    public void Report(RepositoryIndexProgress progress)
    {
        lock (_gate)
        {
            if (_disposed) return;
            var changed = _latest is null || _latest.Phase != progress.Phase
                || _latest.Step != progress.Step || _latest.WorkingRoot != progress.WorkingRoot
                || _latest.Pause != progress.Pause;
            _save(progress);
            _latest = progress;
            if (changed) Write();
        }
    }

    internal void Tick()
    {
        lock (_gate)
        {
            if (_disposed || _latest is null || _latest.Phase is RepositoryIndexProgressPhase.Completed or RepositoryIndexProgressPhase.Failed)
                return;
            // A heartbeat proves the process is alive, not that its counters advanced. Keep
            // the record fresh for --follow without changing any completed-work figures.
            try
            {
                _latest = _latest with { UpdatedAtUtc = _time.GetUtcNow() };
                _save(_latest);
                Write();
            }
            catch (IOException) { /* A closed observer must not crash a background timer. */ }
            catch (UnauthorizedAccessException) { /* The next foreground report surfaces a save failure. */ }
            catch (ObjectDisposedException) { /* The output surface was closed. */ }
        }
    }

    private void Write()
    {
        if (_operation is not null) _output.WriteLine(_operation());
        _output.WriteLine(Catalogue.Format("Elapsed", Format(_latest!),
            _time.GetElapsedTime(_started).TotalSeconds.ToString("0", CultureInfo.InvariantCulture)));
    }

    internal static string Format(RepositoryIndexProgress progress)
    {
        var name = Catalogue.Get(progress.Phase.ToString());
        if (progress.Phase is RepositoryIndexProgressPhase.Completed or RepositoryIndexProgressPhase.Failed)
            return name;
        if (progress.Step is { } step) name += ": " + Catalogue.Get(step.ToString());
        if (progress.Pause is { } pause) name += ": " + Catalogue.Get(pause.ToString());
        var done = progress.Phase.CountsChunks() ? progress.ProcessedChunks : progress.StepDone;
        var total = progress.Phase.CountsChunks() ? progress.TotalChunks : progress.StepTotal;
        if (total <= 0) return Catalogue.Format("Uncounted", name);
        var line = Catalogue.Format("Counted", name, done, total,
            (100d * done / total).ToString("0.#", CultureInfo.InvariantCulture));
        if (progress.Phase.CountsChunks())
            line += Catalogue.Format("Rate", progress.ChunksPerSecond.ToString("0.#", CultureInfo.InvariantCulture),
                progress.EstimatedRemaining is { } left
                    ? left.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) : "?");
        return line;
    }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; }
        _timer.Dispose();
    }
}
