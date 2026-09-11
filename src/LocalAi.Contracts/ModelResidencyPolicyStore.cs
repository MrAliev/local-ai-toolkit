using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalAi.Contracts;

/// <summary>
/// The values a policy takes when nobody chose one. Its own type because a record's positional
/// parameter cannot default to a constant declared in that record's body, and the alternative —
/// the number written once in the parameter list and once in <see cref="BrokerPolicy.Default"/> —
/// is two places to change and one to forget.
/// </summary>
public static class BrokerPolicyDefaults
{
    /// <summary>
    /// How long a model nothing is asking for stays loaded.
    ///
    /// It used to be zero, and zero is the one value that makes the common case pay the most.
    /// Work arrives in a run rather than one piece at a time: an index build posts its next
    /// batch while the previous one is still being worked on, so the queue is empty only for the
    /// instant between two batches — and at zero that instant is enough for the idle sweep to
    /// unload eight gigabytes the next batch immediately loads again. Measured where this was
    /// found: fifty-seven embedding batches, fifty-seven unloads, and a median request of ten
    /// seconds with two to four of them spent loading the model inside it.
    ///
    /// Ten seconds is chosen to cover that gap and little else. It is not the answer to "how
    /// long is a model worth holding" in general, which is what a person setting this deliberately
    /// is answering; it is the answer to "long enough that consecutive work does not reload it".
    ///
    /// It does not delay a model switch, which is what zero was protecting. Work for another
    /// model unloads the resident one on the execution path, in
    /// <c>ModelRuntime.EnsureReadyAsync</c>, before its own preflight — proved by
    /// <c>ModelRuntimeTests.Ensure_ready_unloads_other_managed_models_before_preflight</c>. The
    /// idle sweep runs only when nothing is queued at all.
    /// </summary>
    public const int IdleModelKeepAliveSeconds = 10;

    /// <summary>
    /// The largest embedding batch a machine will let the adaptive policy grow to.
    ///
    /// 256 is the product's own maximum, and this is how a card whose runner drops a
    /// request that size stops having it re-proved on every build: the policy climbs, the
    /// runner goes away, it steps back, and three successes later it climbs again.
    /// </summary>
    public const int MaxEmbeddingBatchItems = 256;
}

public sealed record BrokerPolicy(
    int SchemaVersion,
    ModelResidencyPolicy ModelResidency,
    int IdleModelKeepAliveSeconds = BrokerPolicyDefaults.IdleModelKeepAliveSeconds,
    int MaxEmbeddingBatchItems = BrokerPolicyDefaults.MaxEmbeddingBatchItems)
{
    public const string FileName = "policy.json";

    public static BrokerPolicy Default { get; } = new(
        1,
        ModelResidencyPolicy.RequireFullVram,
        BrokerPolicyDefaults.IdleModelKeepAliveSeconds,
        BrokerPolicyDefaults.MaxEmbeddingBatchItems);
}

/// <summary>
/// Installation-wide broker policy, stored in the runtime root so the broker, the CLI and the
/// installer all read the same document instead of each holding its own opinion.
///
/// A missing, empty or malformed file yields <see cref="BrokerPolicy.Default"/>. A machine
/// that was never configured and a machine whose policy file got corrupted must both land on
/// the strict setting — a parse error must never silently relax a safety check.
/// </summary>
public sealed class ModelResidencyPolicyStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false,
    };

    private readonly string _runtimeRoot;

    public ModelResidencyPolicyStore(string runtimeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        _runtimeRoot = runtimeRoot;
    }

    /// <summary>
    /// Where this reads from: the settings directory, falling back to the loose file an
    /// installation from before the split still has. Resolved per call rather than in the
    /// constructor, because a migration can move the file while this object is alive.
    /// </summary>
    private string ReadPath =>
        RuntimeDirectories.SettingsFile(_runtimeRoot, BrokerPolicy.FileName);

    /// <summary>Where it writes: always the settings directory, never the legacy path.</summary>
    private string WritePath =>
        RuntimeDirectories.SettingsFileForWriting(_runtimeRoot, BrokerPolicy.FileName);

    /// <summary>
    /// The runtime root every LocalAi component uses when none was passed explicitly.
    /// </summary>
    public static string DefaultRuntimeRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LocalAi");

    public static BrokerPolicy ReadDefault() =>
        new ModelResidencyPolicyStore(DefaultRuntimeRoot).Read();

    public BrokerPolicy Read() => ReadWithSource().Policy;

    /// <summary>The policy, and whether the file on disk is what produced it.</summary>
    public PolicyRead<BrokerPolicy> ReadWithSource()
    {
        var path = ReadPath;
        var found = File.Exists(path);
        try
        {
            if (!found)
            {
                return new PolicyRead<BrokerPolicy>(BrokerPolicy.Default, path, false, false);
            }

            var policy = JsonSerializer.Deserialize<BrokerPolicy>(
                File.ReadAllBytes(path),
                SerializerOptions);
            if (policy is null ||
                policy.SchemaVersion != BrokerPolicy.Default.SchemaVersion ||
                !Enum.IsDefined(policy.ModelResidency) ||
                policy.IdleModelKeepAliveSeconds < 0)
            {
                return new PolicyRead<BrokerPolicy>(BrokerPolicy.Default, path, true, false);
            }

            return new PolicyRead<BrokerPolicy>(policy, path, true, true);
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return new PolicyRead<BrokerPolicy>(BrokerPolicy.Default, path, found, false);
        }
    }

    public void Write(BrokerPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var path = WritePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(policy, SerializerOptions));
        RuntimeDirectories.DiscardLegacySettingsFile(_runtimeRoot, BrokerPolicy.FileName);
    }
}
