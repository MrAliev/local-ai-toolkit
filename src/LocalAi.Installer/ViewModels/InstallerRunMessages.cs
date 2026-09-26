using System.Text;
using LocalAi.Installer.Core;
using LocalAi.Installer.Core.Transactions;

namespace LocalAi.Installer.ViewModels;

/// <summary>
/// Facts captured by the wizard after settling the run. Presentation receives values, not
/// mutable UI state, an executor or the authority to change consent and completed effects.
/// </summary>
internal sealed record InstallerRunMessageFacts(
    bool WasCancelled,
    bool HasRunError,
    bool NoValidatedModels,
    bool HasRollbackReport,
    bool CanRollback,
    string? ReportSaveFailure,
    string? ClientNextSteps,
    bool PathAdded);

/// <summary>
/// Formats finish and recovery messages from recorded facts. It neither discovers effects
/// nor performs recovery; journal/executor outcomes remain the source of truth. The wizard
/// may render again after a language change without rerunning any installation work.
/// </summary>
internal static class InstallerRunMessages
{
    public static string? NextSteps(InstallerRunMessageFacts outcome)
    {
        var next = new List<string>();
        if (outcome.WasCancelled)
        {
            next.Add(InstallerCulture.Pick(
                "Installation stopped. Changes already made are listed above.",
                "Установка остановлена. Выполненные изменения перечислены выше."));
        }
        else if (outcome.HasRunError)
        {
            next.Add(InstallerCulture.Pick(
                "Installation did not complete successfully. Completed and unfinished actions are listed above.",
                "Установка завершилась с проблемами. Выполненные и незавершённые действия перечислены выше."));
        }

        if (outcome.HasRunError)
        {
            next.Add(InstallerCulture.Pick(
                "Keep the report or copy the technical details below. After addressing the reported cause, close this window and run the installer again.",
                "Сохраните отчёт или скопируйте технические сведения ниже. Устраните указанную причину, закройте это окно и снова запустите установщик."));
        }

        if (outcome.NoValidatedModels)
        {
            next.Add(InstallerCulture.Pick(
                "No selected model passed validation. If a model was refused for lack of memory, choose a smaller model or context on the next run; if its check failed, read its details above before retrying.",
                "Ни одна выбранная модель не прошла проверку. Если модели отказано из-за нехватки памяти, в следующем запуске выберите меньшую модель или контекст; если проверка завершилась ошибкой, прочитайте её сведения выше перед повторной попыткой."));
        }

        if (outcome.HasRollbackReport)
        {
            next.Add(InstallerCulture.Pick(
                "A rollback was attempted. Its report below identifies what was undone, retained, or failed; original installation results remain above.",
                "Выполнена попытка отката. В отчёте ниже указано, что отменено, сохранено или завершилось ошибкой; выше остаются исходные результаты установки."));
        }
        else if (outcome.HasRunError)
        {
            next.Add(outcome.CanRollback
                ? InstallerCulture.Pick(
                    "The rollback button can undo recorded reversible changes only. Shared dependencies and downloaded models are retained.",
                    "Кнопка отката отменяет только записанные обратимые изменения. Общие зависимости и скачанные модели сохраняются.")
                : InstallerCulture.Pick(
                    "Automatic rollback is unavailable for this run. Changes already made remain in place.",
                    "Автоматический откат для этого запуска недоступен. Выполненные изменения остаются на месте."));
        }

        if (outcome.ReportSaveFailure is { } saveFailure)
        {
            next.Add(string.Format(InstallerCulture.Pick(
                "Could not save the report: {0}. Copy the technical details below before closing this window.",
                "Не удалось сохранить отчёт: {0}. Скопируйте технические сведения ниже перед закрытием окна."), saveFailure));
        }

        if (!outcome.HasRollbackReport && outcome.ClientNextSteps is { } restart)
        {
            next.Add(restart);
        }

        if (outcome.PathAdded)
        {
            next.Add(InstallerCulture.Pick(
                "Open a new terminal to use the updated PATH.",
                "Откройте новый терминал, чтобы использовать обновлённый PATH."));
        }

        return next.Count == 0 ? null : string.Join("\n\n", next);
    }

    public static string InterruptedRunNotice(InstallerRunJournalSnapshot snapshot)
    {
        var notice = new StringBuilder();
        notice.AppendLine(string.Format(
            InstallerCulture.Pick(
                "A previous installer run ({0:yyyy-MM-dd HH:mm} UTC) was interrupted before " +
                "it could finish. What it recorded:",
                "Предыдущий запуск установщика ({0:yyyy-MM-dd HH:mm} UTC) был " +
                "прерван до завершения. Что он записал:"),
            snapshot.StartedAtUtc));
        foreach (var step in snapshot.Steps)
        {
            var state = step.Status switch
            {
                InstallerRunStepStatus.Completed =>
                    InstallerCulture.Pick("applied", "применено"),
                InstallerRunStepStatus.Running => InstallerCulture.Pick(
                    "started, state unknown",
                    "начато, состояние неизвестно"),
                InstallerRunStepStatus.Failed =>
                    InstallerCulture.Pick("failed", "не удалось"),
                _ => step.Status.ToString(),
            };
            notice.AppendLine($"  - {step.Description}: {state}.");
        }

        if (snapshot.Steps.Count == 0)
        {
            notice.AppendLine(InstallerCulture.Pick(
                "  - Nothing was applied before it stopped.",
                "  - До остановки ничего не применялось."));
        }

        notice.Append(snapshot.HasReversibleWork
            ? InstallerCulture.Pick(
                "You can roll back the reversible changes now, or continue and leave them " +
                "in place.",
                "Обратимые изменения можно откатить сейчас или продолжить " +
                "и оставить их как есть.")
            : InstallerCulture.Pick(
                "Nothing it recorded is reversible by this installer. Continue to leave it " +
                "as it is.",
                "Ничего из записанного этот установщик откатить не может. " +
                "Продолжите, чтобы оставить как есть."));
        return notice.ToString();
    }

    /// <summary>
    /// Undone, left in place, failed — by name, effect by effect. The finish page used to
    /// show "RollbackNotes" that were only ever the run log; this text exists so the page
    /// never again implies more was undone than actually was.
    /// </summary>
    public static string RollbackReport(InstallerRollbackReport rollbackResult)
    {
        var text = new StringBuilder();
        text.AppendLine(rollbackResult.AllReversibleUndone
            ? "Rollback finished. Effect by effect:"
            : "Rollback finished with failures. Effect by effect:");
        foreach (var step in rollbackResult.Steps)
        {
            var verdict = step.Outcome switch
            {
                InstallerRollbackStepOutcome.Undone => "undone",
                InstallerRollbackStepOutcome.LeftInPlace => "left in place",
                InstallerRollbackStepOutcome.Skipped => "left alone",
                _ => "rollback FAILED",
            };
            text.AppendLine($"  - {step.Description}: {verdict}. {step.Detail}");
        }

        return text.ToString().TrimEnd();
    }
}
