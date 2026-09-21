using LocalAi.Installer.Core;
using System.Globalization;
using LocalAi.Contracts;
using LocalAi.Installer.Core.Models;
using LocalAi.Installer.Core.Planning;

namespace LocalAi.Installer.ViewModels;

/// <summary>
/// Wizard pages in navigation order. Progress is a page of its own so the confirmation page
/// stays a pure milestone — summary and consent — as the Windows wizard guidelines require.
/// </summary>
public enum InstallerPage
{
    Diagnose,
    Dependencies,
    Package,
    Models,
    Agents,
    Confirm,
    Progress,
    Finish,
}

public enum ModelSelectionMode
{
    Automatic,

    /// <summary>
    /// Pick one catalogue model and one of the context sizes it permits. This replaced a
    /// free-text field that accepted any string: a model outside the routing catalogue
    /// cannot be loaded, so that field could only ever configure a failure.
    /// </summary>
    ChooseExact,

    Skip,
}

/// <summary>
/// Integration choice per client app, mapped one-to-one onto
/// <see cref="AgentIntegrationChoice"/>. The wizard does not invent its own vocabulary, so
/// there is exactly one way to say "leave this client alone".
/// </summary>
public enum AgentChoice
{
    McpOnly,
    InstructionsOnly,
    McpAndInstructions,
    NoChange,
}

public static class AgentChoiceMapping
{
    public static AgentIntegrationChoice ToCore(this AgentChoice choice) =>
        choice switch
        {
            AgentChoice.McpOnly => AgentIntegrationChoice.McpOnly,
            AgentChoice.InstructionsOnly => AgentIntegrationChoice.InstructionsOnly,
            AgentChoice.McpAndInstructions => AgentIntegrationChoice.McpAndInstructions,
            AgentChoice.NoChange => AgentIntegrationChoice.NoChange,
            _ => throw new ArgumentOutOfRangeException(nameof(choice), choice, null),
        };

    public static string Title(this AgentChoice choice) =>
        InstallerCulture.Pick(choice.EnglishTitle(), choice.RussianTitle());

    /// <summary>
    /// The title for the run log, which is written to a file beside an English journal and is
    /// therefore English whatever the window is speaking. Without this the log read
    /// "Claude: Зарегистрировать MCP-серверы applied to …".
    /// </summary>
    public static string EnglishTitle(this AgentChoice choice) =>
        choice switch
        {
            AgentChoice.McpOnly => "Register MCP servers",
            AgentChoice.InstructionsOnly => "Install instructions block",
            AgentChoice.McpAndInstructions => "Register MCP servers and install instructions",
            AgentChoice.NoChange => "Leave unchanged",
            _ => string.Empty,
        };

    private static string RussianTitle(this AgentChoice choice) =>
        choice switch
        {
            AgentChoice.McpOnly => "Зарегистрировать MCP-серверы",
            AgentChoice.InstructionsOnly => "Установить блок инструкций",
            AgentChoice.McpAndInstructions =>
                "Зарегистрировать MCP-серверы и установить инструкции",
            AgentChoice.NoChange => "Оставить без изменений",
            _ => string.Empty,
        };

    public static string Description(this AgentChoice choice) =>
        choice switch
        {
            AgentChoice.McpOnly => InstallerCulture.Pick(
                "Adds the LocalAi code search and local model servers to this client.",
                "Добавляет этому клиенту серверы поиска по коду и локальных " +
                "моделей LocalAi."),
            AgentChoice.InstructionsOnly => InstallerCulture.Pick(
                "Adds the managed LocalAi instructions block, without touching server " +
                "registrations.",
                "Добавляет управляемый блок инструкций LocalAi, не трогая " +
                "регистрации серверов."),
            AgentChoice.McpAndInstructions => InstallerCulture.Pick(
                "Both of the above. This is what a first-time setup normally wants.",
                "И то и другое. Обычно это то, что нужно при первой настройке."),
            AgentChoice.NoChange => InstallerCulture.Pick(
                "This client is left exactly as it is.",
                "Этот клиент остаётся ровно таким, как есть."),
            _ => string.Empty,
        };
}

public enum CheckStatus
{
    Ok,
    Warning,
    Missing,
    Blocking,
}

/// <summary>
/// One line on the diagnosis page. The page exists to show what was actually found on the
/// machine, so each check carries its own detail rather than collapsing to a single boolean.
/// </summary>
public sealed class EnvironmentCheck(string Name, CheckStatus Status, string Detail) : ObservableObject
{
    public string Name { get; private set; } = Name;

    public CheckStatus Status { get; private set; } = Status;

    public string Detail { get; private set; } = Detail;

    /// <summary>
    /// Takes another row's content and keeps its own place in the table. A row replaced by an
    /// equal one — a tool's name, version and path do not translate — dropped out of the
    /// automation tree, so the table is reworded in place.
    /// </summary>
    internal void Become(EnvironmentCheck other)
    {
        Name = other.Name;
        Status = other.Status;
        Detail = other.Detail;
        AnnounceEverything();
    }

    public string StatusText => Status switch
    {
        CheckStatus.Ok => InstallerCulture.Pick("OK", "ОК"),
        CheckStatus.Warning => InstallerCulture.Pick("Warning", "Внимание"),
        CheckStatus.Missing => InstallerCulture.Pick("Not found", "Не найдено"),
        CheckStatus.Blocking => InstallerCulture.Pick("Unsupported", "Не поддерживается"),
        _ => string.Empty,
    };
}

public sealed class DependencySelection(string Id, string Title, bool IsRequired) : ObservableObject
{
    public string Id { get; } = Id;

    public string Title { get; } = Title;

    public bool IsRequired { get; } = IsRequired;

    public bool IsConsented { get; set; }

    public bool IsInstalled { get; set; }

    /// <summary>
    /// False when no automated recipe exists. Such an item is shown as information rather
    /// than as a checkbox that would quietly do nothing.
    /// </summary>
    public bool IsInstallable { get; init; } = true;

    public string StateText => IsInstalled
        ? InstallerCulture.Pick("Already installed", "Уже установлен")
        : InstallerCulture.Pick("Not installed", "Не установлен");

    private Func<string> consequence = static () => string.Empty;

    /// <summary>
    /// What is given up by skipping this. "Optional" on its own invites skipping everything
    /// and finding the cost later, at the point where a tool quietly stops answering
    /// precisely — which is the worst moment to learn it.
    /// </summary>
    public string Consequence
    {
        get => consequence();
        init => consequence = () => value;
    }

    /// <summary>Both languages, read each time the row is drawn.</summary>
    public (string English, string Russian) ConsequenceWording
    {
        init => consequence = () => InstallerCulture.Pick(value.English, value.Russian);
    }

    /// <summary>
    /// Whether the wizard refuses to continue without it. Shown, because a list where
    /// every line looks equally mandatory makes an optional item feel like an obligation —
    /// and most of this list is exactly that: three to four gigabytes and several UAC
    /// prompts for capabilities a given user may not want.
    /// </summary>
    public string RequirementText => IsRequired
        ? InstallerCulture.Pick("required", "обязателен")
        : Consequence.Length == 0
            ? InstallerCulture.Pick("optional", "необязателен")
            // A colon, not the em dash this line is already joined by: ".NET SDK 10 — Not
            // installed · optional — without it, …" made the reader work out which dash
            // separated what.
            : string.Format(
                InstallerCulture.Pick("optional: {0}", "необязателен: {0}"),
                Consequence);

    /// <summary>
    /// The half a reader scans: the name, and whether this machine already has it.
    /// <see cref="RequirementText"/> is the line underneath, because joined on here it ran the row
    /// off the page — and the half that was cut was the one saying what skipping costs.
    /// </summary>
    public string HeadLine => Title + " — " + StateText;

    public string AccessibleName => ActionText + " " + Title;

    public string ActionText => !IsInstallable
        ? InstallerCulture.Pick("Install manually", "Установить вручную")
        : IsInstalled
            ? InstallerCulture.Pick("Reinstall", "Переустановить")
            : InstallerCulture.Pick("Install", "Установить");
}

/// <summary>One entry of the client-integration combo box: the choice, and its own title.</summary>
public sealed class AgentChoiceOption(AgentChoice Choice) : ObservableObject
{
    public AgentChoice Choice { get; } = Choice;

    public string Title => Choice.Title();

    /// <summary>
    /// The closed combo box draws a template <c>DisplayMemberPath</c> never reaches; the template
    /// is in the XAML, and this is the floor under it: a binding that breaks again degrades to the
    /// right words rather than to a type name.
    /// </summary>
    public override string ToString() => Title;
}

public sealed record RecommendedModel(string Id, string Purpose, string Detail);

/// <summary>
/// The mark beside a row, shared by every list in this window that shows states. One vocabulary,
/// so a tick means the same thing on the install page and on the one after it — and so the
/// markup that draws them exists once.
/// </summary>
public enum RunMark
{
    None,
    Busy,
    Done,
    Warned,
    Failed,
}

/// <summary>One line of what a run did, on the page it ends on.</summary>
public sealed record RunResultRow(RunMark Mark, string Title, string Detail);

/// <summary>
/// Where one model's row is. <see cref="NotReached"/> is not a synonym for a failure: a batch
/// stops at the first one, so every model after it gets no result at all — a state the run
/// produced from the start and nothing could show.
/// </summary>
public enum ModelRunState
{
    Waiting,
    Working,
    Ready,
    Rejected,
    Failed,
    NotReached,
    Cancelled,
    Unknown,
    ReadyWithLimitations,
}

/// <summary>
/// One model on the install page. Six of them take tens of minutes, and the page used to show a
/// single sentence for all of it: which model the run was on, which were done and which were
/// never tried were in the log afterwards and nowhere while it mattered.
/// </summary>
public sealed class ModelRunRow(string tag, int contextTokens) : ObservableObject
{
    private ModelRunState state = ModelRunState.Waiting;

    /// <summary>What the detail is worded from, so the words can follow the language.</summary>
    private ModelProvisioningProgress? step;
    private (BrokerModelInstallOutcome Outcome, bool Downloaded, IReadOnlyList<ModelFallbackSuggestion> Smaller, string Code, bool Degraded)? verdict;

    public string Tag { get; } = tag;

    public int ContextTokens { get; } = contextTokens;

    public ModelRunState State
    {
        get => state;
        private set
        {
            SetProperty(ref state, value);
            OnPropertyChanged(nameof(Mark));
        }
    }

    /// <summary>
    /// Waiting and never-reached share a mark: both are a row nothing has happened to, and the
    /// difference between them is what <see cref="Detail"/> is for.
    /// </summary>
    public RunMark Mark => State switch
    {
        ModelRunState.Working => RunMark.Busy,
        ModelRunState.Ready => RunMark.Done,
        ModelRunState.ReadyWithLimitations => RunMark.Warned,
        ModelRunState.Rejected => RunMark.Warned,
        ModelRunState.Failed => RunMark.Failed,
        ModelRunState.Unknown => RunMark.Warned,
        _ => RunMark.None,
    };

    /// <summary>The tag has a column of its own, so this never repeats it.</summary>
    public string Detail => State switch
    {
        ModelRunState.NotReached => PageLabels.ModelNotReached,
        ModelRunState.Cancelled when verdict is null => PageLabels.ModelCancelled,
        ModelRunState.Unknown => PageLabels.ModelResultUnknown,
        _ when verdict is { } settled => Settled(settled),
        _ when step is { } reported => ModelStepWording.ForARow(reported),
        _ => PageLabels.ModelWaiting,
    };

    private static string Settled(
        (BrokerModelInstallOutcome Outcome, bool Downloaded, IReadOnlyList<ModelFallbackSuggestion> Smaller, string Code, bool Degraded) verdict) =>
        verdict.Outcome switch
        {
            BrokerModelInstallOutcome.Accepted => verdict.Degraded
                ? verdict.Downloaded ? PageLabels.ModelLimitedDownloaded : PageLabels.ModelLimitedAlreadyHere
                : verdict.Downloaded ? PageLabels.ModelReadyDownloaded : PageLabels.ModelReadyAlreadyHere,
            BrokerModelInstallOutcome.RejectedResidency => verdict.Smaller.Count == 0
                ? PageLabels.ModelDoesNotFit
                : PageLabels.ModelDoesNotFit + " " + string.Format(
                    CultureInfo.InvariantCulture,
                    PageLabels.ModelSmallerOptions,
                    string.Join(
                        ", ",
                        verdict.Smaller.Select(choice => $"{choice.Model} / {choice.ContextTokens}"))),
            BrokerModelInstallOutcome.Cancelled => PageLabels.ModelCancelled,
            _ => string.Format(CultureInfo.InvariantCulture, PageLabels.ModelFailed, verdict.Code),
        };

    internal void Relabel() => OnPropertyChanged(nameof(Detail));

    public void Apply(ModelProvisioningProgress step)
    {
        ArgumentNullException.ThrowIfNull(step);
        // Progress can be posted before a verdict and delivered after it. Only the batch's
        // authoritative result may enrich a terminal row through Settled.
        if (State is not (ModelRunState.Waiting or ModelRunState.Working))
        {
            return;
        }

        this.step = step;
        State = step.Phase switch
        {
            ModelProvisioningPhase.Planned => ModelRunState.Waiting,
            ModelProvisioningPhase.Accepted when step.ResidencyDegraded => ModelRunState.ReadyWithLimitations,
            ModelProvisioningPhase.Accepted => ModelRunState.Ready,
            ModelProvisioningPhase.Rejected => ModelRunState.Rejected,
            _ => ModelRunState.Working,
        };
        OnPropertyChanged(nameof(Detail));
    }

    /// <summary>
    /// The verdict, taken apart rather than passed whole: the batch result's constructor is the
    /// installer core's own, and a row that can only be exercised through a real run is a row
    /// whose states are decided by whichever ones a machine happened to produce.
    /// </summary>
    public void Settled(
        BrokerModelInstallOutcome outcome,
        bool downloaded,
        IReadOnlyList<ModelFallbackSuggestion> smaller,
        string code,
        bool residencyDegraded = false)
    {
        ArgumentNullException.ThrowIfNull(smaller);
        verdict = (outcome, downloaded, smaller, code, residencyDegraded);
        State = outcome switch
        {
            BrokerModelInstallOutcome.Accepted when residencyDegraded => ModelRunState.ReadyWithLimitations,
            BrokerModelInstallOutcome.Accepted => ModelRunState.Ready,
            BrokerModelInstallOutcome.RejectedResidency => ModelRunState.Rejected,
            BrokerModelInstallOutcome.Cancelled => ModelRunState.Cancelled,
            _ => ModelRunState.Failed,
        };
        OnPropertyChanged(nameof(Detail));
    }

    public void NeverReached()
    {
        State = ModelRunState.NotReached;
        OnPropertyChanged(nameof(Detail));
    }

    public void FinishWithoutVerdict(bool cancelled)
    {
        if (State == ModelRunState.Waiting)
        {
            NeverReached();
        }
        else if (State == ModelRunState.Working)
        {
            State = cancelled ? ModelRunState.Cancelled : ModelRunState.Unknown;
            OnPropertyChanged(nameof(Detail));
        }
    }
}

public sealed class AgentOption(string Agent, AgentChoice Choice) : ObservableObject
{
    private bool isDetected;

    public string Agent { get; } = Agent;

    public AgentChoice Choice { get; private set; } = Choice;

    public string DisplayName => Agent switch
    {
        "claude" => "Claude Code",
        "codex" => "Codex",
        _ => Agent,
    };

    public bool IsDetected
    {
        get => isDetected;
        init => isDetected = value;
    }

    /// <summary>The page changes a row in place; the row tells its bindings.</summary>
    internal void Set(AgentChoice choice, bool detected)
    {
        Choice = choice;
        isDetected = detected;
        AnnounceEverything();
    }

    public string DetectionText => IsDetected
        ? InstallerCulture.Pick("detected", "обнаружено")
        : InstallerCulture.Pick("not detected", "не обнаружено");

    /// <summary>
    /// The whole row heading, assembled here rather than out of four &lt;Run&gt;s in the markup.
    /// Runs concatenate in source order, which is the one thing a translation cannot keep: the
    /// bracket in Russian sits where the sentence puts it, not where the XAML did.
    /// </summary>
    public string Heading => $"{DisplayName} ({DetectionText})";

    public string AccessibleName => string.Format(
        InstallerCulture.Pick("Integration with {0}", "Интеграция с {0}"), DisplayName);

    /// <summary>
    /// Whether this row carries the text-search gate underneath its combo. Claude Code alone: the
    /// gate is a <c>PreToolUse</c> hook in that client's own settings, and Codex has no such
    /// event to hang it on (#423).
    /// </summary>
    public bool HasTextSearchGate =>
        string.Equals(Agent, "claude", StringComparison.Ordinal);
}

/// <summary>
/// One entry in the orientation pane on the left of the wizard. It carries no page of its
/// own: the pane renders a title and a state, and both wizards — installation and removal —
/// have their own page enumerations to keep track of where they are.
/// </summary>
public sealed record WizardStep(string Title, bool IsCurrent, bool IsDone);
