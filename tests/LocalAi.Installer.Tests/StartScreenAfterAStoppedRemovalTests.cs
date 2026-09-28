using LocalAi.Contracts.Activation;
using LocalAi.Installer.Core;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.ViewModels;
using LocalAi.TestFixtures;

namespace LocalAi.Installer.Tests;

/// <summary>
/// A removal cancelled after bin left settings, jobs, telemetry, unknown entries and the PATH
/// entry behind, and the start screen called that nothing installed and offered only Install.
/// The Apps &amp; features entry was already gone, so nothing led back to the removal (#614).
///
/// Without a pointer both installing and removing what is left are offered, and with two
/// errands open, one of which deletes things, neither is chosen for the reader.
/// </summary>
public sealed class StartScreenAfterAStoppedRemovalTests : IDisposable
{
    private readonly RemovalFixture machine = new();

    public void Dispose() => machine.Dispose();

    [Fact]
    public void Leftovers_are_named_and_both_ways_out_are_offered()
    {
        var start = Start(ExistingLocalAiState.Leftovers);

        Assert.Equal(
            "LocalAi is not installed, but some of its files are still on this computer.",
            start.Headline);
        Assert.Equal(
            "You can install it again, or remove what is left. Nothing is changed until you " +
            "confirm it on a review page.",
            start.Detail);
        AssertInstallAndRemoveOnly(start);
    }

    [Fact]
    public void The_signing_keys_alone_are_mentioned_and_both_ways_out_are_offered()
    {
        var start = Start(ExistingLocalAiState.SigningKeysOnly);

        Assert.Equal("LocalAi is not installed on this computer.", start.Headline);
        Assert.Equal(
            "Install it to give your assistants local code search and local models. The " +
            "release signing keys from an earlier installation are still here; removing " +
            "LocalAi can take them too.",
            start.Detail);
        AssertInstallAndRemoveOnly(start);
    }

    [Fact]
    public void Leftovers_read_in_russian()
    {
        InstallerCulture.Current = InstallerLanguage.Russian;

        var start = Start(ExistingLocalAiState.Leftovers);

        Assert.Equal(
            "LocalAi не установлен, но часть его файлов ещё на этом компьютере.",
            start.Headline);
        Assert.Equal(
            "Можно установить его заново или удалить то, что осталось. Ничего не изменится, " +
            "пока вы это не подтвердите на итоговой странице.",
            start.Detail);
        Assert.Equal(
            "Нечего обновлять — ничего не установлено.",
            start.Option(StartChoice.UpdateOrRepair).UnavailableReason);
        Assert.Equal(
            "Нечего переустанавливать — ничего не установлено.",
            start.Option(StartChoice.CleanReinstall).UnavailableReason);
    }

    [Fact]
    public void The_signing_keys_alone_read_in_russian()
    {
        InstallerCulture.Current = InstallerLanguage.Russian;

        var start = Start(ExistingLocalAiState.SigningKeysOnly);

        Assert.Equal("LocalAi не установлен на этом компьютере.", start.Headline);
        Assert.Equal(
            "Установите его, чтобы дать вашим ассистентам локальный поиск по коду и локальные " +
            "модели. Ключи подписи релизов от прежней установки ещё здесь; удаление LocalAi " +
            "может убрать и их.",
            start.Detail);
    }

    /// <summary>The machine the evidence describes: bin gone, the rest of the root still there.</summary>
    [Fact]
    public void A_root_whose_bin_is_gone_offers_the_removal_again()
    {
        Directory.Delete(Path.Combine(machine.Runtime, "bin"), recursive: true);

        var start = new InstallerStartViewModel(
            machine.LocalAppData,
            new ExistingLocalAiInspector(new SystemFileSystemProbe(), () => null),
            () => new InstalledVersion(null, null));

        Assert.Equal(ExistingLocalAiState.Leftovers, start.State);
        Assert.True(start.Option(StartChoice.Remove).IsAvailable);
        Assert.True(start.Option(StartChoice.Install).IsAvailable);
    }

    /// <summary>
    /// Nothing is installed either way, but "this will be a first installation" was untrue where
    /// an earlier one left something: the install half of a clean reinstall, whose window says
    /// "reinstall", read it over the kept indexes and settings (RC31 sandbox run
    /// 20260928-153719-clean-reinstall, install-half\01-install.uia.txt).
    /// </summary>
    [Theory]
    [InlineData(ExistingLocalAiState.Absent, "none — this will be a first installation")]
    [InlineData(ExistingLocalAiState.Leftovers, "none — parts of an earlier installation are still on this computer")]
    [InlineData(ExistingLocalAiState.SigningKeysOnly, "none — the release signing keys from an earlier installation are still here")]
    public void The_system_check_says_what_an_earlier_installation_left(ExistingLocalAiState state, string detail)
    {
        var page = new DiagnosePageViewModel();

        page.Load(HermeticDiagnosis.Supported(existingLocalAi: state), registryAnswered: true);

        var row = page.Checks.Single(check => check.Name == "Existing LocalAi");
        Assert.Equal(CheckStatus.Ok, row.Status);
        Assert.Equal(detail, row.Detail);
    }

    private static void AssertInstallAndRemoveOnly(InstallerStartViewModel start)
    {
        var install = start.Option(StartChoice.Install);
        Assert.True(install.IsAvailable);
        Assert.Equal(
            "Sets up the prerequisites, the runtime and the client integrations.",
            install.Description);
        var repair = start.Option(StartChoice.UpdateOrRepair);
        Assert.False(repair.IsAvailable);
        Assert.Equal("Nothing is installed to update.", repair.UnavailableReason);
        var reinstall = start.Option(StartChoice.CleanReinstall);
        Assert.False(reinstall.IsAvailable);
        Assert.Equal("Nothing is installed to reinstall.", reinstall.UnavailableReason);
        var remove = start.Option(StartChoice.Remove);
        Assert.True(remove.IsAvailable);
        Assert.Equal(
            "Choose what goes and what stays, then take it off this computer.",
            remove.Description);
        Assert.Null(start.Selected);
        Assert.All(start.Actions, option => Assert.False(option.IsSelected, option.Title));
        Assert.False(start.HasProblem);
    }

    private InstallerStartViewModel Start(ExistingLocalAiState state) =>
        new(
            machine.LocalAppData,
            new StubInspector(state),
            () => new InstalledVersion(null, null));

    private sealed class StubInspector(ExistingLocalAiState state) : IExistingLocalAiInspector
    {
        public ExistingLocalAiSnapshot Inspect(string localAppData) => new(state, null, null, null);
    }
}
