using LocalAi.Installer.Core.Removal;

namespace LocalAi.Installer.Core.Tests;

/// <summary>
/// The preview is the sentence somebody agrees to. Its section headings already come from
/// <see cref="RemovalMatrix"/>, so leaving the verbs beside them in English produces a box that
/// is Russian down the left and English down the middle — the reader has to know both to know
/// what is about to be deleted.
///
/// Paths stay paths in either language: they are what the file system calls the thing.
/// </summary>
public sealed class RemovalPreviewSpeaksRussianTests : IDisposable
{
    private readonly InstallerLanguage original = InstallerCulture.Current;

    public void Dispose() => InstallerCulture.Current = original;

    [Fact]
    public void Every_line_of_the_preview_is_russian()
    {
        InstallerCulture.Current = InstallerLanguage.Russian;

        var text = Plan().PreviewText;

        Assert.Contains("  удалить C:\\LocalAi\\bin\\", text, StringComparison.Ordinal);
        Assert.Contains("  оставить release-signing", text, StringComparison.Ordinal);
        Assert.Contains("Остаётся в C:\\LocalAi:", text, StringComparison.Ordinal);
        Assert.Contains("Оставлено: Ollama models — ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_that_would_change_nothing_says_so_in_russian()
    {
        InstallerCulture.Current = InstallerLanguage.Russian;

        var empty = Plan() with { Paths = [], RemovesAppsAndFeaturesEntry = false };
        var unselected = empty with { Selection = RemovalSelection.Nothing() };

        Assert.Contains(
            "Ничего из выбранного не найдено: этот запуск ничего не изменит.",
            empty.PreviewText,
            StringComparison.Ordinal);
        Assert.Contains(
            "Ничего не выбрано: этот запуск ничего не изменит.",
            unselected.PreviewText,
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_english_preview_is_left_exactly_as_it_was()
    {
        InstallerCulture.Current = InstallerLanguage.English;

        var text = Plan().PreviewText;

        Assert.Contains("  remove C:\\LocalAi\\bin\\", text, StringComparison.Ordinal);
        Assert.Contains("Left in C:\\LocalAi:", text, StringComparison.Ordinal);
        Assert.Contains("  keep release-signing", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The entry goes before the copy it runs, so no entry is left offering a removal whose
    /// program is gone.
    /// </summary>
    [Fact]
    public void The_registration_line_says_the_entry_goes_before_the_copy_it_runs()
    {
        var plan = Plan() with { RemovesAppsAndFeaturesEntry = true };

        InstallerCulture.Current = InstallerLanguage.English;
        var english = plan.PreviewText;
        InstallerCulture.Current = InstallerLanguage.Russian;
        var russian = plan.PreviewText;

        Assert.Contains(
            "  remove the Apps & features entry, then the uninstaller copy it runs" +
            Environment.NewLine,
            english,
            StringComparison.Ordinal);
        Assert.Contains(
            "  удалить запись в «Приложениях и возможностях», затем копию деинсталлятора, " +
            "которую она запускает" + Environment.NewLine,
            russian,
            StringComparison.Ordinal);
    }

    private static UninstallPlan Plan() =>
        new(
            RuntimeRoot: "C:\\LocalAi",
            Selection: RemovalSelection.FromPreset(RemovalPreset.FullUninstall),
            Paths: [new RemovalPathEntry(RemovalItem.Binaries, "C:\\LocalAi\\bin", true)],
            RetainedPaths: ["release-signing"],
            AgentConfigurations: [],
            Hooks: [],
            Retained: [new RetainedNotice("Ollama models", "They may serve other tools.")]);
}
