using LocalAi.Installer.Core;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.Core.Removal;
using LocalAi.Installer.ViewModels;
using LocalAi.TestFixtures;

namespace LocalAi.Installer.Tests;

/// <summary>
/// A removal cancelled after bin ended on a finish page that said to run the installer again,
/// and there was nothing left to run it from: the Apps &amp; features entry had gone first (#614).
/// The finish page now offers to remove what is left from the same window, back through the
/// same choices and a review page built from what is on disk now.
/// </summary>
public sealed class ARemovalThatStoppedCanBeFinishedTests : IDisposable
{
    private const string Usual =
        "Start from one of the three presets and change any row you like. Nothing is removed " +
        "until you confirm it on the next page.";

    private const string NotInstalled =
        "LocalAi is not installed here; these rows apply to what is left of it, and the next " +
        "page lists every path found.";

    private const string AfterAnError =
        "Check the outcome below before retrying. Removal does not restore deleted data.";

    private const string TheRestCanGo =
        "What was not removed is listed below; the rest can be removed from this window now, " +
        "and the list is shown again before anything is deleted.";

    private readonly RemovalFixture machine = new();

    public void Dispose() => machine.Dispose();

    private string Bin => Path.Combine(machine.Runtime, "bin");

    [Fact]
    public async Task A_working_installation_opens_on_the_usual_description()
    {
        var wizard = await Wizard();

        Assert.Equal(Usual, wizard.StepDescription);
    }

    [Fact]
    public async Task Without_bin_the_first_page_says_what_its_rows_apply_to()
    {
        Directory.Delete(Bin, recursive: true);

        var wizard = await Wizard();

        Assert.False(wizard.IsBlocked);
        Assert.Equal(NotInstalled + " " + Usual, wizard.StepDescription);
    }

    [Fact]
    public async Task Without_bin_the_first_page_says_so_in_russian()
    {
        Directory.Delete(Bin, recursive: true);
        var wizard = await Wizard();

        InstallerCulture.Current = InstallerLanguage.Russian;

        Assert.Equal(
            "LocalAi здесь не установлен; эти строки относятся к тому, что от него осталось, а " +
            "на следующей странице перечислены все найденные пути. Начните с одного из трёх " +
            "наборов и меняйте любые строки. Ничего не будет удалено, пока вы не подтвердите " +
            "это на следующей странице.",
            wizard.StepDescription);
    }

    [Fact]
    public async Task A_broken_installation_is_not_a_working_one_either()
    {
        var wizard = await Wizard(inspector: new StubInspector(ExistingLocalAiState.Unrecognized));

        Assert.Equal(NotInstalled + " " + Usual, wizard.StepDescription);
    }

    /// <summary>
    /// The PATH step runs after the files, so the entry can outlive the whole root. The start
    /// screen offers the removal for it, and the wizard must not answer that nothing is here.
    /// </summary>
    [Fact]
    public async Task The_launcher_left_on_the_path_is_something_to_remove_with_the_root_gone()
    {
        Directory.Delete(machine.Runtime, recursive: true);

        var wizard = await Wizard(inspector: new StubInspector(ExistingLocalAiState.Leftovers));

        Assert.False(wizard.IsBlocked);
        Assert.True(wizard.CanMoveNext);
        Assert.Equal(NotInstalled + " " + Usual, wizard.StepDescription);
    }

    [Fact]
    public async Task A_cancelled_removal_says_the_rest_can_go_from_here_and_offers_it()
    {
        var wizard = await Confirmed(await Wizard());

        Assert.False(await RunCancelledAfterBin(wizard));

        Assert.True(wizard.IsFinishPage);
        Assert.Equal(AfterAnError + " " + TheRestCanGo, wizard.StepDescription);
        Assert.True(wizard.CanRemoveWhatIsLeft);
        Assert.True(wizard.RemoveWhatIsLeftCommand.CanExecute(null));
        Assert.Contains(
            "Check the journal before closing this window and running the installer again.",
            wizard.Summary,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_finish_page_says_it_in_russian()
    {
        var wizard = await Confirmed(await Wizard());
        Assert.False(await RunCancelledAfterBin(wizard));

        InstallerCulture.Current = InstallerLanguage.Russian;

        Assert.Equal(
            "Перед повторной попыткой проверьте результат ниже. Удаление не восстанавливает " +
            "удалённые данные. Что не удалено, перечислено ниже; удалить оставшееся можно прямо " +
            "из этого окна — список снова покажут перед удалением.",
            wizard.StepDescription);
    }

    /// <summary>
    /// Back on the first page with the choices made before, a repository left out included, and
    /// the disk read again: the review page lists only what is still there.
    /// </summary>
    [Fact]
    public async Task Removing_what_is_left_goes_back_to_the_same_choices_and_looks_again()
    {
        var token = TestContext.Current.CancellationToken;
        var wizard = await Wizard();
        Row(wizard, RemovalItem.Settings).IsSelected = false;
        wizard.Repositories.Single(repository => repository.RepositoryId == "husky").IsSelected = false;
        await Confirmed(wizard);
        Assert.False(await RunCancelledAfterBin(wizard));

        Assert.True(await wizard.RemoveWhatIsLeftAsync(token));

        Assert.Equal(UninstallPage.Choose, wizard.CurrentPage);
        Assert.False(wizard.HasRunError);
        Assert.False(wizard.HasReport);
        Assert.False(wizard.IsConfirmed);
        Assert.False(wizard.CanRemoveWhatIsLeft);
        Assert.Equal(RemovalPreset.FullUninstall, wizard.SelectedPreset);
        Assert.All(wizard.Rows, row => Assert.Equal(row.Item != RemovalItem.Settings, row.IsSelected));
        var husky = wizard.Repositories.Single(repository => repository.RepositoryId == "husky");
        Assert.True(husky.CanChoose);
        Assert.False(husky.IsSelected);
        Assert.Equal(NotInstalled + " " + Usual, wizard.StepDescription);

        Assert.True(await wizard.MoveNextAsync(token));
        Assert.DoesNotContain(Bin, wizard.PreviewText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Path.Combine(machine.Runtime, "jobs"), wizard.PreviewText, StringComparison.Ordinal);
        wizard.IsConfirmed = true;
        Assert.True(await wizard.RunAsync(token));

        Assert.Equal("Removal complete", wizard.StepTitle);
        Assert.False(wizard.CanRemoveWhatIsLeft);
        Assert.False(Directory.Exists(Path.Combine(machine.Runtime, "jobs")));
        Assert.True(File.Exists(Path.Combine(machine.Runtime, RemovalMatrix.SettingsFileNames.First())));
        Assert.True(File.Exists(Path.Combine(machine.HuskyHooks, "post-commit")));
    }

    [Fact]
    public async Task A_failed_removal_offers_it_too()
    {
        var token = TestContext.Current.CancellationToken;
        var wizard = await Confirmed(await Wizard());
        using (new FileStream(
                   Path.Combine(machine.Runtime, "jobs", "job.json"),
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.None))
        {
            Assert.False(await wizard.RunAsync(token));
        }

        Assert.Equal(AfterAnError + " " + TheRestCanGo, wizard.StepDescription);
        Assert.True(wizard.CanRemoveWhatIsLeft);

        Assert.True(await wizard.RemoveWhatIsLeftAsync(token));
        Assert.True(await wizard.MoveNextAsync(token));
        wizard.IsConfirmed = true;
        Assert.True(await wizard.RunAsync(token));
        Assert.False(Directory.Exists(Path.Combine(machine.Runtime, "jobs")));
    }

    [Fact]
    public async Task A_refused_removal_offers_it_too()
    {
        var wizard = await Confirmed(await Wizard(new Refusing()));

        Assert.False(await wizard.RunAsync(TestContext.Current.CancellationToken));

        Assert.StartsWith("Nothing was removed.", wizard.Summary, StringComparison.Ordinal);
        Assert.Equal(AfterAnError + " " + TheRestCanGo, wizard.StepDescription);
        Assert.True(wizard.CanRemoveWhatIsLeft);
    }

    [Fact]
    public async Task A_removal_that_finished_offers_nothing_more()
    {
        var token = TestContext.Current.CancellationToken;
        var wizard = await Confirmed(await Wizard());

        Assert.True(await wizard.RunAsync(token));

        Assert.Equal("Everything selected was removed.", wizard.StepDescription);
        Assert.False(wizard.CanRemoveWhatIsLeft);
        Assert.False(wizard.RemoveWhatIsLeftCommand.CanExecute(null));
        Assert.False(await wizard.RemoveWhatIsLeftAsync(token));
        Assert.True(wizard.IsFinishPage);
    }

    private static RemovalRow Row(UninstallWizardViewModel wizard, RemovalItem item) =>
        wizard.Rows.Single(row => row.Item == item);

    private async Task<UninstallWizardViewModel> Wizard(
        IProcessRunner? processRunner = null,
        IExistingLocalAiInspector? inspector = null)
    {
        var wizard = new UninstallWizardViewModel(
            RemovalPreset.FullUninstall,
            false,
            machine.Layout,
            machine.Home,
            processRunner ?? new Stopped(),
            machine.HooksPathReader,
            inspector: inspector ?? new ExistingLocalAiInspector(new SystemFileSystemProbe(), () => null))
        {
            LogDirectory = Path.Combine(machine.Root, "reports"),
        };
        await wizard.InitializeAsync(TestContext.Current.CancellationToken);
        return wizard;
    }

    private static async Task<UninstallWizardViewModel> Confirmed(UninstallWizardViewModel wizard)
    {
        Assert.True(await wizard.MoveNextAsync(TestContext.Current.CancellationToken));
        wizard.IsConfirmed = true;
        return wizard;
    }

    /// <summary>Stops the run once bin, the first path a full removal takes, is gone.</summary>
    private async Task<bool> RunCancelledAfterBin(UninstallWizardViewModel wizard)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var finished = await wizard.RunAsync(stop.Token, new Observe(step =>
        {
            if (step.Text.EndsWith(Bin, StringComparison.Ordinal) && !Directory.Exists(Bin))
            {
                stop.Cancel();
            }
        }));
        Assert.False(Directory.Exists(Bin));
        Assert.True(Directory.Exists(Path.Combine(machine.Runtime, "jobs")));
        return finished;
    }

    private sealed class Observe(Action<UninstallProgress> observe) : IProgress<UninstallProgress>
    {
        public void Report(UninstallProgress value) => observe(value);
    }

    private sealed class Stopped : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments,
            TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(new ProcessResult(0, "", "", false, false));
    }

    /// <summary>The launcher's stop fails, so the run refuses before touching anything.</summary>
    private sealed class Refusing : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments,
            TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(new ProcessResult(1, "", "stop refused", false, false));
    }

    private sealed class StubInspector(ExistingLocalAiState state) : IExistingLocalAiInspector
    {
        public ExistingLocalAiSnapshot Inspect(string localAppData) => new(state, null, null, null);
    }
}
