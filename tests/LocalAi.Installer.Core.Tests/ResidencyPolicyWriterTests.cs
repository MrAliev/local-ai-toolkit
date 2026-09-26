using LocalAi.Contracts;
using LocalAi.Installer.Core.Activation;

namespace LocalAi.Installer.Core.Tests;

public sealed class ResidencyPolicyWriterTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "LocalAi.Installer.Core.Residency.Tests",
        Guid.NewGuid().ToString("N"));

    /// <summary>
    /// The regression this class exists for. Writing the policy used to create the LocalAi
    /// root with a plain CreateDirectory, so a run that failed before installing anything left
    /// behind a directory with an inherited ACL — and the layout lease refuses such a root for
    /// good, which made every later installation on that machine fail with a message naming
    /// the condition and not the cure.
    /// </summary>
    [Fact]
    public void Does_not_create_the_installation_root()
    {
        var outcome = ResidencyPolicyWriter.Apply(root, ModelResidencyPolicy.AllowCpu);

        Assert.Equal(ResidencyPolicyOutcome.SkippedWithoutInstallation, outcome);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void Writes_the_policy_into_an_existing_installation()
    {
        Directory.CreateDirectory(root);

        var outcome = ResidencyPolicyWriter.Apply(
            root,
            ModelResidencyPolicy.AllowPartialOffload);

        Assert.Equal(ResidencyPolicyOutcome.Applied, outcome);
        Assert.Equal(
            ModelResidencyPolicy.AllowPartialOffload,
            new ModelResidencyPolicyStore(root).Read().ModelResidency);
    }

    /// <summary>
    /// Only the residency field is the wizard's to change. Everything else in the policy
    /// document belongs to whoever wrote it and has to survive an installation.
    /// </summary>
    [Fact]
    public void Keeps_the_rest_of_an_existing_policy()
    {
        Directory.CreateDirectory(root);
        var store = new ModelResidencyPolicyStore(root);
        var before = store.Read();
        store.Write(before with { ModelResidency = ModelResidencyPolicy.AllowCpu });

        ResidencyPolicyWriter.Apply(root, ModelResidencyPolicy.RequireFullVram);

        var after = store.Read();
        Assert.Equal(ModelResidencyPolicy.RequireFullVram, after.ModelResidency);
        Assert.Equal(
            before with { ModelResidency = ModelResidencyPolicy.RequireFullVram },
            after);
    }

    /// <summary>
    /// A run that keeps the rule it found has nothing to store. Writing it anyway failed a repair
    /// on a policy file somebody had made read-only, for a value that was already in it.
    /// </summary>
    [Fact]
    public void Leaves_a_policy_that_already_holds_the_choice_alone()
    {
        Directory.CreateDirectory(root);
        var store = new ModelResidencyPolicyStore(root);
        store.Write(store.Read() with { ModelResidency = ModelResidencyPolicy.AllowPartialOffload });
        var path = RuntimeDirectories.SettingsFileForWriting(root, BrokerPolicy.FileName);
        var before = File.ReadAllBytes(path);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var outcome = ResidencyPolicyWriter.Apply(root, ModelResidencyPolicy.AllowPartialOffload);

            Assert.Equal(ResidencyPolicyOutcome.Unchanged, outcome);
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    /// <summary>
    /// The same value in the file an installation from before the settings split still has is
    /// not the same as the value in place: writing it is what moves it to where it is read from.
    /// </summary>
    [Fact]
    public void Still_writes_a_choice_found_only_at_the_legacy_path()
    {
        Directory.CreateDirectory(root);
        var legacy = Path.Combine(root, BrokerPolicy.FileName);
        File.WriteAllText(legacy, "{\"SchemaVersion\":1,\"ModelResidency\":\"AllowCpu\"}");

        var outcome = ResidencyPolicyWriter.Apply(root, ModelResidencyPolicy.AllowCpu);

        Assert.Equal(ResidencyPolicyOutcome.Applied, outcome);
        Assert.True(File.Exists(RuntimeDirectories.SettingsFileForWriting(root, BrokerPolicy.FileName)));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
