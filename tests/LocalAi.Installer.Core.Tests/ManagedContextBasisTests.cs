using LocalAi.Installer.Core.Agents;

namespace LocalAi.Installer.Core.Tests;

public sealed class ManagedContextBasisTests
{
    [Fact]
    public void Installer_instructions_do_not_turn_file_reads_into_required_context()
    {
        var installed = ManagedInstructionBlock.Upsert(string.Empty).Content;
        Assert.Contains("ExplicitTaskInput", installed, StringComparison.Ordinal);
        Assert.Contains("necessary for this request", installed, StringComparison.Ordinal);
        Assert.Contains("whole file", installed, StringComparison.Ordinal);
    }
}
