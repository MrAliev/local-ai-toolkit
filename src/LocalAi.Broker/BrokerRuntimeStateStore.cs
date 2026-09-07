using System.Text.Json;
using LocalAi.Contracts;

namespace LocalAi.Broker;

public sealed class BrokerRuntimeStateStore
{
    private readonly string _statePath;

    public BrokerRuntimeStateStore(string runtimeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        var root = Path.GetFullPath(runtimeRoot);
        Directory.CreateDirectory(root);
        _statePath = Path.Combine(root, "host.json");
    }

    /// <summary>
    /// How long the rename keeps retrying while another process holds the state file open.
    ///
    /// Contention here is brief and ordinary — a reader has host.json open for the moment it
    /// takes to parse it — so the budget only has to outlast a read, not a workload.
    /// </summary>
    public static TimeSpan DefaultRetryBudget { get; } = TimeSpan.FromSeconds(2);

    public void Publish(BrokerProcessState state) => Publish(state, DefaultRetryBudget);

    /// <param name="retryBudget">
    /// A duration rather than a number of attempts. Six attempts with a rising sleep came to
    /// about 375ms of patience, which is ample on an idle machine and not on a loaded one: the
    /// test that proves the retry works released its lock after 75ms and still lost the race on
    /// a CI runner. An attempt count silently means different amounts of waiting depending on
    /// what else the machine is doing; a budget means what it says.
    /// </param>
    public void Publish(BrokerProcessState state, TimeSpan retryBudget)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retryBudget, TimeSpan.Zero);
        // Recomputable, and the clearest case of it: what host.json says is where this broker is
        // listening and when it last beat, all of it true only while the process is alive. A copy
        // that survived a power cut would describe a broker that does not exist.
        AtomicFile.WriteJson(
            _statePath,
            state,
            LocalAiJson.Strict,
            AtomicWrite.Recomputable.Retrying(retryBudget));
    }

    public void DeleteIfOwnedBy(BrokerProcessState owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        BrokerProcessState? current;
        try
        {
            current = File.Exists(_statePath)
                ? JsonSerializer.Deserialize<BrokerProcessState>(
                    File.ReadAllText(_statePath),
                    LocalAiJson.Strict)
                : null;
        }
        catch (JsonException)
        {
            return;
        }

        if (current is not null &&
            current.ProcessId == owner.ProcessId &&
            current.StartedAtUtc == owner.StartedAtUtc)
        {
            File.Delete(_statePath);
        }
    }
}
