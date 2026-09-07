using System.Collections.ObjectModel;
using LocalAi.Installer.Core;
using LocalAi.Installer.Core.Diagnosis;

namespace LocalAi.Installer.ViewModels;

/// <summary>
/// Per-client integration choice.
///
/// A detected client defaults to full integration: registering the MCP servers and writing
/// the managed instructions is the entire reason the rest of the installation is worth
/// anything, and defaulting to "leave unchanged" produced installations where the binaries
/// were present and no client could reach them. Nothing is applied silently — the review
/// page lists the choice and the run only starts on explicit confirmation.
///
/// A client that was not detected stays untouched, and an explicit choice always wins over
/// the default, including a deliberate "leave unchanged".
/// </summary>
public sealed class AgentIntegrationPageViewModel : ObservableObject
{
    private readonly HashSet<string> explicitChoices =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What the machine has right now, read rather than remembered.</summary>
    private bool gateOnDisk;

    private bool gateAnsweredByHand;
    private bool refusesTextSearch;

    public ObservableCollection<AgentOption> Agents { get; } =
    [
        new("codex", AgentChoice.NoChange),
        new("claude", AgentChoice.NoChange),
    ];

    public IReadOnlyList<AgentChoice> Choices { get; } =
    [
        AgentChoice.McpAndInstructions,
        AgentChoice.McpOnly,
        AgentChoice.InstructionsOnly,
        AgentChoice.NoChange,
    ];

    /// <summary>
    /// The same four choices with something to display. The combo box was bound straight to the
    /// enum values and had no display projection, so the page where the choice is made read
    /// "McpAndInstructions" and "NoChange" — the titles existed, and only the review page ever
    /// used them.
    /// </summary>
    public IReadOnlyList<AgentChoiceOption> ChoiceOptions { get; }

    public AgentIntegrationPageViewModel() =>
        ChoiceOptions = [.. Choices.Select(choice => new AgentChoiceOption(choice))];

    /// <summary>
    /// Every option, including "leave unchanged", is a valid answer, so there is no state in
    /// which the user has failed to decide and this page never blocks navigation.
    /// </summary>
    public bool CanContinue => true;

    public string ReviewText =>
        InstallerCulture.Pick("Clients: ", "Клиенты: ") + string.Join(
            "; ",
            Agents.Select(agent => $"{agent.DisplayName} — {agent.Choice.Title()}"));

    /// <summary>
    /// Whether the text-search gate can be offered at all: its refusal text sends the reader to
    /// the codesearch tools, so it means nothing unless those servers are being registered — and
    /// that is the choice in the combo directly above the box (#423).
    ///
    /// Claude Code alone, because the gate is a <c>PreToolUse</c> hook in that client's own
    /// settings; Codex's choice is not allowed a say in it.
    /// </summary>
    public bool CanRefuseTextSearch =>
        Agents.Any(agent =>
            string.Equals(agent.Agent, "claude", StringComparison.Ordinal) &&
            agent.Choice is AgentChoice.McpOnly or AgentChoice.McpAndInstructions);

    /// <summary>
    /// What this run will leave on the machine — which is the same thing as what the box shows,
    /// deliberately, in both states it can be in.
    ///
    /// While the box is offered it is the person's answer. While it is not, it is whatever the
    /// machine already has, so withdrawing the servers writes nothing to <c>settings.json</c>:
    /// it neither quietly withdraws a refusal that is registered nor quietly installs one. That
    /// is also why this is a fall-back rather than an assignment — flipping the combo away and
    /// back leaves the person's answer where they left it.
    /// </summary>
    public bool RefusesTextSearch
    {
        get => CanRefuseTextSearch ? refusesTextSearch : gateOnDisk;
        set
        {
            gateAnsweredByHand = true;
            refusesTextSearch = value;
            NotifyGate();
        }
    }

    /// <summary>
    /// The hint under the box, which is a different sentence when the box cannot be ticked: a
    /// disabled control with no reason beside it is a control the reader argues with.
    /// </summary>
    public string TextSearchHint => CanRefuseTextSearch
        ? PageLabels.RefuseTextSearchHint
        : PageLabels.RefuseTextSearchNeedsMcp;

    /// <summary>
    /// Where the entry goes, for the review line to name. Empty until the wizard has read the
    /// machine.
    /// </summary>
    public string GateSettingsPath { get; private set; } = string.Empty;

    /// <summary>
    /// Whether the run has anything to write for the gate: only when the answer differs from what
    /// is on disk. An unticked box over a machine with nothing registered must not open
    /// <c>settings.json</c> at all — an empty <c>hooks: {}</c> is an effect nobody consented to,
    /// and it would reformat somebody's file on the way in.
    /// </summary>
    public bool GateChanges => RefusesTextSearch != gateOnDisk;

    /// <summary>
    /// What the review page says about the gate, in four sentences rather than one with optional
    /// clauses.
    ///
    /// Naming an effect and naming a reversal are not the same sentence, and squeezing both
    /// through one hole is how "what will be configured" came to read as "what is being replaced"
    /// (#256). It is printed on every run, including one that leaves the client alone: on an
    /// update the clients page is folded away, so this is the only place the effect is stated.
    ///
    /// The reversal deliberately stops short of promising that a text search works again. Other
    /// entries may remain on the same event, and the line says only what the code checked.
    /// </summary>
    public string GateReviewText => string.Format(
        (RefusesTextSearch, gateOnDisk) switch
        {
            (true, true) => InstallerCulture.Pick(
                "Text search in Claude Code: refused. grep, rg, findstr, Select-String, Grep " +
                "and Glob are turned down before they run, including the ones you type. " +
                "Recorded in {0}.",
                "Текстовый поиск в Claude Code: отклоняется. grep, rg, findstr, " +
                "Select-String, Grep и Glob не доходят до запуска, включая набранные вами. " +
                "Записано в {0}."),
            (true, false) => InstallerCulture.Pick(
                "Text search in Claude Code: refused (left alone now — this run changes it). " +
                "grep, rg, findstr, Select-String, Grep and Glob are turned down before they " +
                "run, including the ones you type. The line goes into {0}.",
                "Текстовый поиск в Claude Code: отклоняется (сейчас не трогается — этот " +
                "запуск это меняет). grep, rg, findstr, Select-String, Grep и Glob не доходят " +
                "до запуска, включая набранные вами. Строка добавляется в {0}."),
            (false, false) => InstallerCulture.Pick(
                "Text search in Claude Code: left alone. Nothing is written to {0}.",
                "Текстовый поиск в Claude Code: не трогается. В {0} ничего не записывается."),
            (false, true) => InstallerCulture.Pick(
                "Text search in Claude Code: left alone (refused now — this run changes it). " +
                "LocalAi's line is taken out of {0}; anything else in that file stays.",
                "Текстовый поиск в Claude Code: не трогается (сейчас отклоняется — этот " +
                "запуск это меняет). Строка LocalAi убирается из {0}; всё остальное в этом " +
                "файле остаётся."),
        },
        GateSettingsPath);

    /// <summary>
    /// Seeds the box from the machine, in the shape the residency and update-check settings are
    /// seeded: read, never remembered. Nothing records that a person declined, so an update
    /// proposes what is already there rather than restoring something they turned off — and
    /// somebody who removed the entry by hand is shown that, instead of a memory of their answer.
    ///
    /// Called more than once in a session, like detection, so an answer given by hand survives it.
    /// </summary>
    public void ApplyGateRegistration(bool registered, string settingsPath)
    {
        gateOnDisk = registered;
        GateSettingsPath = settingsPath ?? string.Empty;
        if (!gateAnsweredByHand)
        {
            refusesTextSearch = registered;
        }

        OnPropertyChanged(nameof(GateSettingsPath));
        NotifyGate();
    }

    public void SetChoice(string agent, AgentChoice choice)
    {
        for (var index = 0; index < Agents.Count; index++)
        {
            if (!string.Equals(Agents[index].Agent, agent, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            explicitChoices.Add(Agents[index].Agent);
            Agents[index] = Agents[index] with { Choice = choice };
            OnPropertyChanged(nameof(Agents));
            OnPropertyChanged(nameof(ReviewText));
            NotifyGate();
            return;
        }

        throw new InvalidOperationException($"Unknown agent '{agent}'.");
    }

    /// <summary>
    /// Records which clients were actually found, so the page can say so instead of silently
    /// offering to configure something that is not installed, and arms the default: a client
    /// that exists is set up unless the user says otherwise.
    ///
    /// Detection runs more than once during a session, so a client that disappears between
    /// runs must lose the default it was given; only a choice the user made by hand is kept
    /// across refreshes.
    /// </summary>
    public void ApplyDetection(IReadOnlyList<AgentSnapshot> agents)
    {
        ArgumentNullException.ThrowIfNull(agents);
        for (var index = 0; index < Agents.Count; index++)
        {
            var kind = string.Equals(Agents[index].Agent, "claude", StringComparison.Ordinal)
                ? AgentKind.Claude
                : AgentKind.Codex;
            var detected = agents.Any(agent =>
                agent.Kind == kind &&
                (agent.Executable.Exists || agent.Config.Exists));
            var choice = explicitChoices.Contains(Agents[index].Agent)
                ? Agents[index].Choice
                : detected
                    ? AgentChoice.McpAndInstructions
                    : AgentChoice.NoChange;
            Agents[index] = Agents[index] with { IsDetected = detected, Choice = choice };
        }

        OnPropertyChanged(nameof(Agents));
        OnPropertyChanged(nameof(ReviewText));
        NotifyGate();
    }

    /// <summary>
    /// Both gate properties read the Claude Code row, so every path that changes that row has to
    /// say so — a checkbox that stays enabled after its condition has gone is a control the
    /// person can tick for an effect the run will not produce.
    /// </summary>
    private void NotifyGate()
    {
        OnPropertyChanged(nameof(CanRefuseTextSearch));
        OnPropertyChanged(nameof(RefusesTextSearch));
        OnPropertyChanged(nameof(GateChanges));
        OnPropertyChanged(nameof(GateReviewText));
        OnPropertyChanged(nameof(TextSearchHint));
    }
}
