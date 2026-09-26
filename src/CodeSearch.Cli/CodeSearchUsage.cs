using LocalAi.Contracts;
using LocalAi.Contracts.Localization;

namespace CodeSearch.Cli;

/// <summary>The shared command descriptions plus an explanation of the index model.</summary>
public static class CodeSearchUsage
{
    public static string Text => CliHelp.Render("codesearch") + Environment.NewLine + Environment.NewLine
        + OutputCulture.Pick(
        """
        One BASE index per repository, and one OVERLAY per worktree holding only what that
        branch changed plus the files it deleted. Searches see the overlay laid over the base,
        so a branch pays for its diff rather than for a second full index.

        For a repository connected to LocalAi both are built and published by `localai sync` -
        the same sync the Git hooks run - and both live under the LocalAi runtime directory.
        Where the base index really lives, whether either has drifted, and whether this
        repository is connected at all are what `codesearch status` prints.

        `index` and `overlay` are the builders underneath. On a connected repository neither is
        the way to refresh anything: run `localai sync`. Use them with explicit `--index` and
        `--overlay` paths, or on a repository LocalAi does not manage.
        """, """
        Один базовый индекс на репозиторий; отдельный оверлей рабочего дерева содержит изменения
        ветки и удалённые файлы. Поиск объединяет базовый индекс и оверлей.

        Для подключённого репозитория обновляйте оба через localai sync. Команда codesearch status
        показывает подключение, путь базового индекса и актуальность обоих индексов.

        index и overlay — низкоуровневые сборщики. Используйте их с явными путями --index и
        --overlay либо для репозиториев, которыми LocalAi не управляет.
        """);
}
