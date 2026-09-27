using System.Text.Json;
using System.Text.Json.Serialization;
using LocalAi.Contracts;
using LocalAi.Installer.Core.Activation;

namespace LocalAi.Installer.Core.Models;

/// <summary>
/// The model setup the installation ran with, so an update or repair offers it again rather than
/// the default a blank machine gets. A setting by where it lives, so a reinstall that keeps
/// settings keeps it.
/// </summary>
public static class ModelSetupRecord
{
    public const string FileName = "model-setup.json";

    private const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>What was recorded, or null when nothing was or the record cannot be trusted.</summary>
    public static ModelProvisioningSelection? Read(string runtimeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        try
        {
            var path = RuntimeDirectories.SettingsFile(runtimeRoot, FileName);
            if (!File.Exists(path))
            {
                return null;
            }

            var record = JsonSerializer.Deserialize<Document>(File.ReadAllBytes(path), SerializerOptions);
            return record switch
            {
                null => null,
                { SchemaVersion: not CurrentSchemaVersion } => null,
                { Mode: ModelProvisioningMode.None } => ModelProvisioningSelection.None,
                { Mode: ModelProvisioningMode.Automatic } => new(ModelProvisioningMode.Automatic),
                { Mode: ModelProvisioningMode.Exact, Model: { Length: > 0 } model, ContextTokens: > 0 } =>
                    new(ModelProvisioningMode.Exact, model, record.ContextTokens),
                _ => null,
            };
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes only into an installation that exists, for the reason <see cref="ResidencyPolicyWriter"/>
    /// gives: a root this creates would be refused by every later installation.
    /// </summary>
    public static ResidencyPolicyOutcome Apply(string runtimeRoot, ModelProvisioningSelection selection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        ArgumentNullException.ThrowIfNull(selection);
        if (!Directory.Exists(runtimeRoot))
        {
            return ResidencyPolicyOutcome.SkippedWithoutInstallation;
        }

        if (Read(runtimeRoot) == selection)
        {
            return ResidencyPolicyOutcome.Unchanged;
        }

        AtomicFile.WriteJson(
            RuntimeDirectories.SettingsFileForWriting(runtimeRoot, FileName),
            new Document(CurrentSchemaVersion, selection.Mode, selection.Model, selection.ContextTokens),
            SerializerOptions,
            AtomicWrite.Irrecoverable);
        return ResidencyPolicyOutcome.Applied;
    }

    private sealed record Document(int SchemaVersion, ModelProvisioningMode Mode, string? Model, int ContextTokens);
}
