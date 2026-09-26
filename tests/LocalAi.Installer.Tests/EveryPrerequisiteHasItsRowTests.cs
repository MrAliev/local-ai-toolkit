using LocalAi.Installer.Core;
using LocalAi.Installer.ViewModels;

namespace LocalAi.Installer.Tests;

/// <summary>
/// The finish page counted prerequisites — "installed 0 of 2, failed 2" — and said "fix the
/// reported cause" over a cause reported nowhere. Each one now has a row of its own, and each
/// cause its own words and its own link, last and without a period so it can be copied.
/// </summary>
public sealed class EveryPrerequisiteHasItsRowTests
{
    private static readonly Uri Git = new("https://git-scm.com/install/windows");
    private static readonly Uri Scip = new("https://www.npmjs.com/package/@sourcegraph/scip-typescript");

    public static TheoryData<PrerequisiteOutcome, RunMark, string, string> Causes() => new()
    {
        { new("Git", PrerequisiteOutcomeKind.Installed), RunMark.Done, "installed", "установлен" },
        { new("Git", PrerequisiteOutcomeKind.Reinstalled), RunMark.Done, "reinstalled", "переустановлен" },
        {
            new("Git", PrerequisiteOutcomeKind.WinGetNotFound, Git),
            RunMark.Failed,
            "not installed: WinGet was not found. Install it yourself: https://git-scm.com/install/windows",
            "не установлен: WinGet не найден. Установите вручную: https://git-scm.com/install/windows"
        },
        {
            new("Git", PrerequisiteOutcomeKind.WinGetRefused, Git),
            RunMark.Failed,
            "not installed: winget on this computer failed the installer's safety check. The log below " +
            "says why and what to do; you can also install it yourself: https://git-scm.com/install/windows",
            "не установлен: winget на этом компьютере не прошёл проверку безопасности установщика. " +
            "Почему и что делать — сказано в журнале ниже; можно также установить вручную: " +
            "https://git-scm.com/install/windows"
        },
        {
            new("Git", PrerequisiteOutcomeKind.ExitCode, Git, Tool: "winget", ExitCode: -1978335212),
            RunMark.Failed,
            "not installed: winget ended with exit code -1978335212. Install it yourself: " +
            "https://git-scm.com/install/windows",
            "не установлен: winget завершился с кодом -1978335212. Установите вручную: " +
            "https://git-scm.com/install/windows"
        },
        {
            new("SCIP TypeScript", PrerequisiteOutcomeKind.TimedOut, Scip, Tool: "npm", Minutes: 10),
            RunMark.Failed,
            "not installed: npm did not finish within 10 min and was stopped. Install it yourself: " +
            "https://www.npmjs.com/package/@sourcegraph/scip-typescript",
            "не установлен: npm не завершился за 10 мин и был остановлен. Установите вручную: " +
            "https://www.npmjs.com/package/@sourcegraph/scip-typescript"
        },
        {
            new("Git", PrerequisiteOutcomeKind.CouldNotStart, Git, Tool: "winget", Reason: "The system cannot find the file specified."),
            RunMark.Failed,
            "not installed: winget could not be started (The system cannot find the file specified.). " +
            "Install it yourself: https://git-scm.com/install/windows",
            "не установлен: не удалось запустить winget (The system cannot find the file specified.). " +
            "Установите вручную: https://git-scm.com/install/windows"
        },
        {
            new("SCIP TypeScript", PrerequisiteOutcomeKind.NpmNotFound, Scip),
            RunMark.Failed,
            "not installed: npm was not found. With Node.js 20 installed, run this installer again",
            "не установлен: npm не найден. Когда Node.js 20 будет установлен, снова запустите установщик"
        },
        {
            new("SCIP Python", PrerequisiteOutcomeKind.CheckAfterInstallFailed),
            RunMark.Failed,
            "installed by npm, but the check after installing failed. The log below says what went wrong",
            "npm установил пакет, но проверка после установки не прошла. Что пошло не так — сказано " +
            "в журнале ниже"
        },
        {
            new("Git", PrerequisiteOutcomeKind.Cancelled, Git),
            RunMark.Failed,
            "not installed: the run was cancelled",
            "не установлен: запуск отменён"
        },
        {
            new("Git", PrerequisiteOutcomeKind.NoAutomatedInstaller),
            RunMark.None,
            "skipped: there is no automated installer for it",
            "пропущено: автоматической установки нет"
        },
    };

    [Theory]
    [MemberData(nameof(Causes))]
    public void Each_cause_has_its_own_words_in_both_languages(
        PrerequisiteOutcome outcome,
        RunMark mark,
        string english,
        string russian)
    {
        var row = RunResults.Prerequisite(outcome);
        Assert.Equal(outcome.Title, row.Title);
        Assert.Equal(mark, row.Mark);
        Assert.Equal(english, row.Detail);

        InstallerCulture.Current = InstallerLanguage.Russian;
        Assert.Equal(russian, RunResults.Prerequisite(outcome).Detail);
    }

    /// <summary>
    /// Without WinGet the required ones cannot be ticked, so a run can select nothing and still
    /// leave the machine without Git — and "nothing was selected" alone reads as nothing wanted.
    /// </summary>
    [Fact]
    public void Nothing_selected_while_required_ones_are_missing_names_them()
    {
        var row = RunResults.Prerequisites(0, 0, 0, ["Git", "Ollama"]);
        Assert.Equal(RunMark.Warned, row.Mark);
        Assert.Equal("nothing was selected; still needed, install by hand: Git, Ollama", row.Detail);

        InstallerCulture.Current = InstallerLanguage.Russian;
        Assert.Equal(
            "ничего не выбрано; ещё нужны, установить вручную: Git, Ollama",
            RunResults.Prerequisites(0, 0, 0, ["Git", "Ollama"]).Detail);
    }
}
