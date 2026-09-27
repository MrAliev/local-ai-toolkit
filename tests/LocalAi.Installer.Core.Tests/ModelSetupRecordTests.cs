using LocalAi.Contracts;
using LocalAi.Installer.Core.Activation;
using LocalAi.Installer.Core.Models;

namespace LocalAi.Installer.Core.Tests;

/// <summary>
/// The model setup an installation ran with, kept so an update offers it again. Updating an
/// installation set up with "skip model setup" offered to set up all seven models (Windows Sandbox
/// run 20260927-040916-update-from-rc19, check U04-models-line).
/// </summary>
public sealed class ModelSetupRecordTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "localai-model-setup-record-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
        }
    }

    public static TheoryData<ModelProvisioningSelection> Selections() =>
    [
        ModelProvisioningSelection.None,
        new ModelProvisioningSelection(ModelProvisioningMode.Automatic),
        new ModelProvisioningSelection(ModelProvisioningMode.Exact, "qwen3-embedding:8b-q8_0", 8192),
    ];

    [Theory]
    [MemberData(nameof(Selections))]
    public void Each_mode_reads_back_as_it_was_recorded(ModelProvisioningSelection selection)
    {
        Directory.CreateDirectory(root);

        var outcome = ModelSetupRecord.Apply(root, selection);

        Assert.Equal(ResidencyPolicyOutcome.Applied, outcome);
        Assert.Equal(selection, ModelSetupRecord.Read(root));
        Assert.True(File.Exists(RuntimeDirectories.SettingsFileForWriting(root, ModelSetupRecord.FileName)));
    }

    /// <summary>The rule every stored setting follows: never be the thing that creates the root.</summary>
    [Fact]
    public void A_machine_without_an_installation_is_left_exactly_as_it_was()
    {
        var outcome = ModelSetupRecord.Apply(root, ModelProvisioningSelection.None);

        Assert.Equal(ResidencyPolicyOutcome.SkippedWithoutInstallation, outcome);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void A_later_run_replaces_the_mode_an_earlier_one_recorded()
    {
        Directory.CreateDirectory(root);
        ModelSetupRecord.Apply(root, ModelProvisioningSelection.None);

        ModelSetupRecord.Apply(root, new(ModelProvisioningMode.Automatic));

        Assert.Equal(new ModelProvisioningSelection(ModelProvisioningMode.Automatic), ModelSetupRecord.Read(root));
    }

    [Fact]
    public void A_mode_already_recorded_is_not_written_again()
    {
        Directory.CreateDirectory(root);
        ModelSetupRecord.Apply(root, ModelProvisioningSelection.None);

        Assert.Equal(ResidencyPolicyOutcome.Unchanged, ModelSetupRecord.Apply(root, ModelProvisioningSelection.None));
    }

    [Fact]
    public void An_installation_that_recorded_nothing_has_no_mode()
    {
        Directory.CreateDirectory(root);

        Assert.Null(ModelSetupRecord.Read(root));
    }

    /// <summary>A record nobody can trust offers nothing: the update falls back as if it were absent.</summary>
    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{"schemaVersion":2,"mode":"None","model":null,"contextTokens":0}""")]
    [InlineData("""{"schemaVersion":1,"mode":"Sometimes","model":null,"contextTokens":0}""")]
    [InlineData("""{"schemaVersion":1,"mode":"Exact","model":null,"contextTokens":8192}""")]
    [InlineData("""{"schemaVersion":1,"mode":"Exact","model":"gpt-oss:20b","contextTokens":0}""")]
    public void A_record_that_cannot_be_trusted_has_no_mode(string content)
    {
        var path = RuntimeDirectories.SettingsFileForWriting(root, ModelSetupRecord.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);

        Assert.Null(ModelSetupRecord.Read(root));
    }
}
