using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.Core.Removal;
using LocalAi.Installer.Core;
using LocalAi.Installer.ViewModels;
using LocalAi.TestFixtures;

namespace LocalAi.Installer.Tests;

public sealed class AccessibleControlsTests
{
    [Fact]
    public async Task Real_peers_name_fields_and_choices_in_both_languages_without_changing_consent()
    {
        await WpfTestThread.Run(() =>
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (var resource in new[] { "Light", "Controls", "Brand" })
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri($"/LocalAi.Installer;component/Themes/{resource}.xaml", UriKind.Relative),
                });
            try { CheckButtonTextFollowsItsState(app); CheckThemeGroup(); CheckWindows(); }
            finally { app.Shutdown(); }
        });
    }

    private static void CheckThemeGroup()
    {
        using var machine = new RemovalFixture();
        var presentation = new InstallerPresentation(new InstallerPreferencesStore(Path.Combine(machine.Root, "preferences")));
        var bar = new PreferencesBar { DataContext = presentation };
        foreach (var language in new[] { InstallerLanguage.English, InstallerLanguage.Russian })
        {
            presentation.ChooseLanguage(language);
            bar.Measure(new Size(900, 200));
            bar.Arrange(new Rect(0, 0, 900, 200));
            bar.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            var radios = Descendants(bar).OfType<RadioButton>().ToArray();
            Assert.Equal(3, radios.Length);
            CheckGroup(Peer(bar), radios, language == InstallerLanguage.Russian ? "Тема" : "Theme");
        }
    }

    // A screen reader says what a set of options chooses by the group that holds exactly them.
    private static void CheckGroup(AutomationPeer scope, IEnumerable<RadioButton> radios, string label)
    {
        var options = radios.ToHashSet<UIElement>();
        var group = Assert.Single(PeerTree(scope), peer =>
            peer.GetAutomationControlType() == AutomationControlType.Group &&
            options.SetEquals(PeerTree(peer).OfType<UIElementAutomationPeer>().Select(item => item.Owner).OfType<RadioButton>()));
        Assert.Equal(label, group.GetName());
        var owner = ((UIElementAutomationPeer)group).Owner;
        Assert.False(owner.Focusable);
        Assert.Equal(label, Assert.IsType<TextBlock>(AutomationProperties.GetLabeledBy(owner)).Text);
    }

    private static void CheckGroup(Window window, string groupName, string label)
    {
        var radios = Descendants(window.Content).OfType<RadioButton>().Where(radio => radio.GroupName == groupName).ToArray();
        Assert.NotEmpty(radios);
        CheckGroup(Peer(Ancestors(radios[0]).OfType<ScrollViewer>().First()), radios, label);
    }

    private static IEnumerable<AutomationPeer> PeerTree(AutomationPeer root)
    {
        yield return root;
        foreach (var child in root.GetChildren() ?? [])
        foreach (var nested in PeerTree(child))
            yield return nested;
    }

    private static void CheckButtonTextFollowsItsState(Application app)
    {
        using var machine = new RemovalFixture();
        var presentation = new InstallerPresentation(new InstallerPreferencesStore(Path.Combine(machine.Root, "preferences")));
        foreach (var window in new Window[] { new StartWindow(presentation), new MainWindow(presentation) })
        {
            try
            {
                Layout(window);
                var button = Descendants(window.Content).OfType<Button>().First(control => control.IsDefault);
                // Exercise the actual compiled template, including WPF's generated plain-text
                // and access-key labels. The palette alone cannot prove the rendered colour.
                button.ClearValue(Button.CommandProperty);
                foreach (var palette in new[] { "Light", "Dark", "HighContrast", "Light" })
                {
                    app.Resources.MergedDictionaries[0] = palette == "HighContrast"
                        ? HighContrastPalette.Create()
                        : new ResourceDictionary { Source = new Uri($"/LocalAi.Installer;component/Themes/{palette}.xaml", UriKind.Relative) };
                    foreach (var content in new[] { "Next", "_Next", "Далее" })
                    foreach (var enabled in new[] { true, false })
                    foreach (var primary in new[] { true, false })
                    {
                        button.Content = content;
                        button.IsEnabled = enabled;
                        button.IsDefault = primary;
                        Layout(window);
                        var expected = (SolidColorBrush)app.Resources[!enabled ? "TextDisabled" : primary ? "AccentForeground" : "TextPrimary"];
                        var labels = Descendants(button).OfType<TextBlock>().ToArray();
                        Assert.NotEmpty(labels);
                        foreach (var label in labels)
                            Assert.Equal(expected.Color, Assert.IsType<SolidColorBrush>(label.Foreground).Color);
                    }
                }
            }
            finally { window.Close(); }
        }
    }

    private static void CheckWindows()
    {
        using var machine = new RemovalFixture();
        var presentation = new InstallerPresentation(new InstallerPreferencesStore(Path.Combine(machine.Root, "preferences")));
        foreach (var mode in new[] { StartChoice.Install, StartChoice.UpdateOrRepair })
        {
            var window = new MainWindow(presentation, mode);
            var wizard = (InstallerWizardViewModel)window.DataContext;
            wizard.Dependencies.SetConsent("Git", true);
            wizard.Dependencies.SetInstalled("Ollama", true);
            wizard.Dependencies.Dependencies.Add(new DependencySelection("manual", "Manual tool", false) { IsInstallable = false });
            try
            {
                CheckLogHistory(window, wizard, presentation);
                foreach (var language in new[] { InstallerLanguage.English, InstallerLanguage.Russian })
                {
                    var consent = wizard.Dependencies.Dependencies.Select(row => row.IsConsented).ToArray();
                    var model = (wizard.Models.IsChooseExact, wizard.Models.SelectedModel, wizard.Models.SelectedContext);
                    var clientsBefore = wizard.Agents.Agents.Select(row => row.Choice).ToArray();
                    presentation.ChooseLanguage(language);
                    Assert.Equal(consent, wizard.Dependencies.Dependencies.Select(row => row.IsConsented));
                    Assert.Equal(model, (wizard.Models.IsChooseExact, wizard.Models.SelectedModel, wizard.Models.SelectedContext));
                    Assert.Equal(clientsBefore, wizard.Agents.Agents.Select(row => row.Choice));
                    var russian = language == InstallerLanguage.Russian;
                    ShowPage(window, wizard, InstallerPage.Dependencies);
                    CheckDefaultAction(window, wizard.NextCommand);
                    var dependencies = Descendants(window.Content).OfType<CheckBox>()
                        .Where(control => control.DataContext is DependencySelection).ToArray();
                    Assert.NotEmpty(dependencies);
                    foreach (var control in dependencies)
                    {
                        var row = (DependencySelection)control.DataContext;
                        Assert.Equal(row.ActionText + " " + row.Title, Peer(control).GetName());
                        Assert.Contains(row.RequirementText, Peer(control).GetHelpText(), StringComparison.Ordinal);
                        Assert.Equal(row.IsInstallable, Peer(control).IsEnabled());
                        var wasConsented = row.IsConsented;
                        Assert.IsAssignableFrom<FrameworkElement>(control.Content);
                        if (row.IsInstallable)
                        {
                            var toggle = (IToggleProvider)Peer(control).GetPattern(PatternInterface.Toggle)!;
                            toggle.Toggle();
                            Assert.Equal(!wasConsented, row.IsConsented);
                            Assert.Equal(wasConsented ? ToggleState.Off : ToggleState.On, toggle.ToggleState);
                            toggle.Toggle();
                            Assert.Equal(wasConsented, row.IsConsented);
                        }
                    }

                    ShowPage(window, wizard, InstallerPage.Package);
                    CheckField(window, TextBox.TextProperty, "Package.ReleaseVersion", russian ? "Версия для установки" : "Version to install");
                    CheckField(window, TextBox.TextProperty, "Package.SourceFolder", russian ? "Папка релиза" : "Release folder");

                    ShowPage(window, wizard, InstallerPage.Models);
                    wizard.Models.IsChooseExact = true;
                    Layout(window);
                    foreach (var radio in Descendants(window.Content).OfType<RadioButton>().Where(item => item.GroupName == "Residency"))
                    {
                        Assert.False(string.IsNullOrWhiteSpace(Peer(radio).GetName()));
                        Assert.False(string.IsNullOrWhiteSpace(Peer(radio).GetHelpText()));
                        Assert.Equal(radio.IsChecked == true, ((ISelectionItemProvider)Peer(radio).GetPattern(PatternInterface.SelectionItem)!).IsSelected);
                    }
                    CheckGroup(window, "Residency", russian ? "Видеопамять" : "Video memory");
                    CheckGroup(window, "ModelMode", russian ? "Локальные модели" : "Local models");
                    CheckHelp(window, Bound<RadioButton>(window, ToggleButton.IsCheckedProperty, "Models.IsAutomatic"), wizard.Models.AutomaticSummary);
                    CheckHelp(window, Bound<RadioButton>(window, ToggleButton.IsCheckedProperty, "Models.IsSkip"), PageLabels.SkipModelSetupHint);
                    CheckField(window, Selector.SelectedItemProperty, "Models.SelectedModel", russian ? "Модель для установки" : "Model to install");
                    CheckField(window, Selector.SelectedItemProperty, "Models.SelectedContext", russian ? "Размер контекста модели" : "Model context size");

                    ShowPage(window, wizard, InstallerPage.Agents);
                    var clients = Descendants(window.Content).OfType<ComboBox>().Where(item => item.DataContext is AgentOption).ToArray();
                    Assert.Equal(2, clients.Length);
                    foreach (var client in clients)
                    {
                        var row = (AgentOption)client.DataContext;
                        Assert.Equal((russian ? "Интеграция с " : "Integration with ") + row.DisplayName, Peer(client).GetName());
                        Assert.NotNull(AutomationProperties.GetLabeledBy(client));
                    }
                    foreach (var claude in new[] { AgentChoice.NoChange, AgentChoice.McpAndInstructions })
                    foreach (var codex in new[] { AgentChoice.NoChange, AgentChoice.McpAndInstructions })
                    {
                        wizard.Agents.SetChoice("claude", claude);
                        wizard.Agents.SetChoice("codex", codex);
                        Layout(window);
                        // The worktree sentence is help only in a row that shows it: hidden, it
                        // describes an effect that is not coming.
                        foreach (var client in Descendants(window.Content).OfType<ComboBox>().Where(item => item.DataContext is AgentOption))
                        {
                            var row = (AgentOption)client.DataContext;
                            var hint = Descendants(VisualTreeHelper.GetParent(client)).OfType<TextBlock>()
                                .Single(text => text.Text == wizard.Agents.WorktreePlacementHint);
                            // Only Claude Code's settings get the worktree hooks, and only from a
                            // row that configures it.
                            Assert.Equal(
                                row.HasTextSearchGate && claude != AgentChoice.NoChange ? Visibility.Visible : Visibility.Collapsed,
                                hint.Visibility);
                            Assert.Equal(hint.Visibility == Visibility.Visible ? hint.Text : string.Empty, Peer(client).GetHelpText());
                        }
                        var gate = Descendants(window.Content).OfType<CheckBox>()
                            .Single(item => item.DataContext is AgentOption { HasTextSearchGate: true });
                        CheckHelp(window, gate, wizard.Agents.TextSearchHint);
                    }
                    ShowPage(window, wizard, InstallerPage.Confirm);
                    CheckHelp(window, Bound<CheckBox>(window, ToggleButton.IsCheckedProperty, "Review.EnableUpdateCheck"),
                        PageLabels.UpdateCheckHint, wizard.Review.UpdateCheckDisclosure);
                    var apply = CheckDefaultAction(window, wizard.InstallCommand);
                    Assert.False(apply.IsEnabled);
                    Assert.Throws<ElementNotEnabledException>(() => ((IInvokeProvider)Peer(apply).GetPattern(PatternInterface.Invoke)!).Invoke());
                    var overview = Descendants(window.Content).OfType<EffectsOverview>().Single();
                    CheckOverview();
                    var technical = Descendants(window.Content).OfType<Expander>()
                        .Single(item => Equals(item.Header, PageLabels.TechnicalDetails));
                    technical.IsExpanded = true;
                    Layout(window);
                    CheckOverview();
                    technical.IsExpanded = false;
                    Assert.False(wizard.Review.IsConfirmed);

                    wizard.ModelRuns.Clear();
                    wizard.ModelRuns.Add(new ModelRunRow("long-model-name:instruct-q8_0", 16384));
                    wizard.RefreshNavigationState();
                    ShowPage(window, wizard, InstallerPage.Progress);
                    // The fixture adds a row directly, without starting the model runner.
                    foreach (var viewer in Descendants(window.Content).OfType<ScrollViewer>())
                        BindingOperations.GetBindingExpression(viewer, UIElement.VisibilityProperty)?.UpdateTarget();
                    Layout(window);
                    var modelText = Descendants(window.Content).OfType<TextBlock>().Single(row =>
                        row.Inlines.OfType<System.Windows.Documents.Run>().Any(run => run.Text == "long-model-name:instruct-q8_0"));
                    Assert.Contains("long-model-name:instruct-q8_0", Peer(modelText).GetName(), StringComparison.Ordinal);

                    void CheckOverview()
                    {
                        var rows = Descendants(overview).OfType<TextBox>().ToArray();
                        Assert.Equal(wizard.ReviewGroups.SelectMany(group => group.Lines), rows.Select(row => row.Text));
                        Assert.All(rows, row =>
                        {
                            Assert.True(row.IsReadOnly);
                            Assert.Equal(row.Text, Peer(row).GetName());
                        });
                    }
                }
            }
            finally { window.Close(); }
        }

        var removal = new UninstallWindow(presentation);
        var removalWizard = (UninstallWizardViewModel)removal.DataContext;
        removalWizard.Choices.ListRepositories([
            new RepositoryRow("enabled", Path.Combine(machine.Root, "repo"), 2, null),
            new RepositoryRow("disabled", Path.Combine(machine.Root, "kept"), 0, "external hooks"),
        ]);
        try
        {
            foreach (var language in new[] { InstallerLanguage.English, InstallerLanguage.Russian })
            {
                var selected = removalWizard.Rows.Select(row => row.IsSelected).ToArray();
                var repositories = removalWizard.Repositories.Select(row => row.IsSelected).ToArray();
                presentation.ChooseLanguage(language);
                Assert.Equal(selected, removalWizard.Rows.Select(row => row.IsSelected));
                Assert.Equal(repositories, removalWizard.Repositories.Select(row => row.IsSelected));
                Layout(removal);
                var russian = language == InstallerLanguage.Russian;
                foreach (var control in Descendants(removal.Content).OfType<ToggleButton>())
                {
                    if (control.DataContext is RemovalRow row)
                        Assert.Equal((russian ? "Удалить: " : "Remove: ") + row.Title, Peer(control).GetName());
                    else if (control.DataContext is RepositoryRow repository)
                    {
                        Assert.Equal((russian ? "Изменить хуки репозитория: " : "Change repository hooks: ") + repository.CommonDirectory, Peer(control).GetName());
                        Assert.Equal(repository.CanChoose, Peer(control).IsEnabled());
                        Assert.Equal(repository.StateText, Peer(control).GetHelpText());
                    }
                    else if (control.DataContext is UninstallPresetOption preset)
                    {
                        Assert.Equal(preset.Title, Peer(control).GetName());
                        Assert.Equal(preset.IsSelected, ((ISelectionItemProvider)Peer(control).GetPattern(PatternInterface.SelectionItem)!).IsSelected);
                    }
                }
                CheckGroup(removal, "Preset", russian ? "С чего начать" : "Start from");
                Assert.False(removalWizard.RemoveSigningKeys);
                Assert.False(removalWizard.IsConfirmed);
            }
        }
        finally { removal.Close(); }
        CheckStartKeyboard(presentation);
        CheckRemovalDefault(presentation, machine);
        CheckReinstallContinuation(presentation, machine);
        CheckRemoveWhatIsLeft(presentation, machine);
    }

    /// <summary>
    /// The way back after a removal that did not finish (#614): on the finish page only when the
    /// run left something, named for what it does, and never the default button. The run is
    /// refused at the stop, so nothing on the machine changes.
    /// </summary>
    private static void CheckRemoveWhatIsLeft(InstallerPresentation presentation, RemovalFixture machine)
    {
        var before = RemovalFixture.Snapshot(machine.Runtime);
        var stopped = Removal(presentation, machine, new RefusingStop());
        stopped.InitializeAsync().GetAwaiter().GetResult();
        stopped.MoveNextAsync().GetAwaiter().GetResult();
        stopped.IsConfirmed = true;
        // Before the window is bound: the run finishes off this thread, and the rail it rebuilds
        // may only change on the window's.
        Assert.False(stopped.RunAsync().GetAwaiter().GetResult());
        Assert.Equal(before, RemovalFixture.Snapshot(machine.Runtime));
        var finished = Removal(presentation, machine, new RefusingStop());
        typeof(UninstallWizardViewModel).GetProperty(nameof(finished.CurrentPage))!
            .SetValue(finished, UninstallPage.Finish);
        var opened = Removal(presentation, machine, new RefusingStop());
        foreach (var (wizard, shown) in new[] { (stopped, true), (finished, false), (opened, false) })
        {
            var window = new UninstallWindow(presentation) { DataContext = wizard };
            try
            {
                Layout(window);
                var button = Descendants(window.Content).OfType<Button>()
                    .Single(candidate => ReferenceEquals(candidate.Command, wizard.RemoveWhatIsLeftCommand));
                foreach (var language in new[] { InstallerLanguage.English, InstallerLanguage.Russian })
                {
                    presentation.ChooseLanguage(language);
                    Layout(window);
                    Assert.Equal(shown ? Visibility.Visible : Visibility.Collapsed, button.Visibility);
                    Assert.Equal(shown, button.IsEnabled);
                    Assert.False(button.IsDefault);
                    Assert.Equal(
                        language == InstallerLanguage.Russian ? "Удалить оставшееся" : "Remove what is left",
                        Peer(button).GetName());
                }
            }
            finally { window.Close(); wizard.DetachPresentation(); }
        }
    }

    private static UninstallWizardViewModel Removal(
        InstallerPresentation presentation,
        RemovalFixture machine,
        IProcessRunner processRunner) =>
        new(RemovalPreset.FullUninstall, false, machine.Layout, machine.Home, processRunner,
            machine.HooksPathReader, presentation: presentation,
            inspector: new ExistingLocalAiInspector(new SystemFileSystemProbe(), () => null))
        {
            LogDirectory = Path.Combine(machine.Root, "remove-what-is-left"),
        };

    /// <summary>The launcher's stop fails, so a run refuses before it touches anything.</summary>
    private sealed class RefusingStop : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments,
            TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(new ProcessResult(1, "", "stop refused", false, false));
    }

    private static void CheckReinstallContinuation(InstallerPresentation presentation, RemovalFixture machine)
    {
        foreach (var offersInstall in new[] { false, true })
        {
            var wizard = new UninstallWizardViewModel(RemovalPreset.ReinstallFriendly, offersInstall,
                machine.Layout, machine.Home, readHooksPath: machine.HooksPathReader, presentation: presentation);
            var window = new UninstallWindow(presentation) { DataContext = wizard };
            var requests = 0;
            wizard.InstallRequested += (_, _) => requests++;
            try
            {
                Layout(window);
                var continuation = Descendants(window.Content).OfType<Button>()
                    .Single(button => ReferenceEquals(button.Command, wizard.ContinueToInstallCommand));
                Assert.Equal(Visibility.Collapsed, continuation.Visibility);
                Assert.False(continuation.IsDefault);
                // Select the completed presentation fixture without running removal or opening
                // the installation window. View-model outcome gates have their own coverage.
                typeof(UninstallWizardViewModel).GetProperty(nameof(wizard.CurrentPage))!
                    .SetValue(wizard, UninstallPage.Finish);
                foreach (var language in new[] { InstallerLanguage.English, InstallerLanguage.Russian })
                {
                    presentation.ChooseLanguage(language);
                    Layout(window);
                    Assert.Equal(offersInstall ? Visibility.Visible : Visibility.Collapsed, continuation.Visibility);
                    Assert.Equal(offersInstall, continuation.IsDefault);
                    Assert.Equal(0, requests);
                    if (offersInstall)
                    {
                        Assert.Same(continuation, CheckDefaultAction(window, wizard.ContinueToInstallCommand));
                        Assert.Equal(PageLabels.ContinueToInstall, Peer(continuation).GetName());
                    }
                }
                if (offersInstall)
                {
                    ((IInvokeProvider)Peer(continuation).GetPattern(PatternInterface.Invoke)!).Invoke();
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                    Assert.Equal(1, requests);
                }
                typeof(UninstallWizardViewModel).GetProperty(nameof(wizard.CurrentPage))!
                    .SetValue(wizard, UninstallPage.Choose);
                Layout(window);
                Assert.Equal(Visibility.Collapsed, continuation.Visibility);
                Assert.False(continuation.IsDefault);
            }
            finally { window.Close(); wizard.DetachPresentation(); }
        }
    }

    private static Button CheckDefaultAction(Window window, ICommand command)
    {
        var buttons = Descendants(window.Content).OfType<Button>().ToArray();
        var primary = Assert.Single(buttons, button => button.IsDefault && button.Visibility == Visibility.Visible);
        Assert.Same(command, primary.Command);
        return primary;
    }

    private static void CheckRemovalDefault(InstallerPresentation presentation, RemovalFixture machine)
    {
        var before = RemovalFixture.Snapshot(machine.Runtime);
        var wizard = new UninstallWizardViewModel(RemovalPreset.FullUninstall, false, machine.Layout, machine.Home,
            readHooksPath: machine.HooksPathReader, presentation: presentation);
        wizard.InitializeAsync().GetAwaiter().GetResult();
        var window = new UninstallWindow(presentation) { DataContext = wizard };
        try
        {
            Layout(window);
            CheckDefaultAction(window, wizard.NextCommand);
            wizard.MoveNextAsync().GetAwaiter().GetResult();
            Layout(window);
            var remove = CheckDefaultAction(window, wizard.UninstallCommand);
            Assert.False(remove.IsEnabled);
            Assert.Throws<ElementNotEnabledException>(() => ((IInvokeProvider)Peer(remove).GetPattern(PatternInterface.Invoke)!).Invoke());
            Assert.Equal(before, RemovalFixture.Snapshot(machine.Runtime));
            wizard.IsConfirmed = true;
            Layout(window);
            Assert.True(remove.IsEnabled);
            wizard.IsConfirmed = false;
            Layout(window);
            Assert.False(remove.IsEnabled);
            Assert.Equal(before, RemovalFixture.Snapshot(machine.Runtime));
        }
        finally { window.Close(); wizard.DetachPresentation(); }
    }

    private static void CheckStartKeyboard(InstallerPresentation presentation)
    {
        var window = new StartWindow(presentation)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false,
        };
        var wizard = (InstallerStartViewModel)window.DataContext;
        wizard.Actions.Clear();
        wizard.Actions.Add(new(StartChoice.Install, "Unavailable", "Unavailable", false, "Already installed"));
        wizard.Actions.Add(new(StartChoice.UpdateOrRepair, "Update", "Update", true, ""));
        wizard.Actions.Add(new(StartChoice.CleanReinstall, "Reinstall", "Reinstall", true, ""));
        wizard.Actions.Add(new(StartChoice.Remove, "Remove", "Remove", true, ""));
        wizard.Select(StartChoice.UpdateOrRepair);
        try
        {
            // A real presentation source is required for keyboard focus. The offscreen start
            // window has no Loaded action and never opens an installation/removal wizard.
            window.Show();
            window.UpdateLayout();
            var rows = Descendants(window.Content).OfType<RadioButton>().Where(row => row.GroupName == "Errand").ToArray();
            Keyboard.Focus(rows[1]);
            Assert.Same(rows[1], Keyboard.FocusedElement);
            Key(rows[1], System.Windows.Input.Key.Down);
            Assert.Equal(StartChoice.CleanReinstall, wizard.Selected);
            Assert.Same(rows[2], Keyboard.FocusedElement);
            Key((UIElement)Keyboard.FocusedElement, System.Windows.Input.Key.Space);
            Assert.Equal(StartChoice.CleanReinstall, wizard.Selected);
            Key(rows[2], System.Windows.Input.Key.Up);
            Key(rows[1], System.Windows.Input.Key.Up);
            Assert.Equal(StartChoice.Remove, wizard.Selected);
            Assert.Same(rows[3], Keyboard.FocusedElement);
            Assert.False(Peer(rows[0]).IsEnabled());

            // Changing a live region must leave keyboard focus on the user's chosen row.
            var focus = Keyboard.FocusedElement;
            Assert.Same(rows[3], focus);
            var live = new LiveStatusText { Text = "Checking" };
            ((Grid)window.Content).Children.Add(live);
            window.UpdateLayout();
            DrainAnnouncements();
            live.Text = "Check complete";
            DrainAnnouncements();
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(live));
            Assert.Equal("Check complete", Peer(live).GetName());
            Assert.Same(focus, Keyboard.FocusedElement);
            live.Text = "Проверка завершена";
            DrainAnnouncements();
            Assert.Equal("Проверка завершена", Peer(live).GetName());
            Assert.Same(focus, Keyboard.FocusedElement);

            static void DrainAnnouncements()
            {
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
            }
        }
        finally { window.Close(); }

        static void Key(UIElement target, System.Windows.Input.Key key)
        {
            var source = PresentationSource.FromVisual(target)!;
            var preview = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            target.RaiseEvent(preview);
            if (!preview.Handled)
                target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
                    { RoutedEvent = Keyboard.KeyDownEvent });
            target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
                { RoutedEvent = Keyboard.KeyUpEvent });
        }
    }

    private static void CheckLogHistory(MainWindow window, InstallerWizardViewModel wizard, InstallerPresentation presentation)
    {
        foreach (var page in new[] { InstallerPage.Progress, InstallerPage.Finish })
        {
            ShowPage(window, wizard, page);
            if (page == InstallerPage.Finish)
                Descendants(window.Content).OfType<Expander>().Single(item => Equals(item.Header, PageLabels.Log)).IsExpanded = true;
            wizard.Finish.Progress = string.Join("\r\n", Enumerable.Range(0, 150).Select(i => $"Diagnostic line {i:000}"));
            Layout(window);
            var views = Descendants(window.Content).OfType<LogView>().ToArray();
            var view = page == InstallerPage.Progress ? views.First() : views.Last();
            var log = Descendants(view).OfType<TextBox>().Single();
            var jump = Descendants(view).OfType<Button>().Single();
            AtEnd();
            wizard.Finish.Progress += "\r\nFollowed line";
            Layout(window);
            AtEnd();
            log.ScrollToVerticalOffset(log.ExtentHeight / 2);
            Layout(window);
            log.Select(log.Text.IndexOf("Diagnostic line 075", StringComparison.Ordinal), 19);
            var offset = log.VerticalOffset;
            var selected = log.SelectedText;
            Assert.Equal("Diagnostic line 075", selected);
            Assert.True(offset > 0);
            Assert.True(log.ExtentHeight - log.ViewportHeight - offset > 10);
            wizard.Finish.Progress += "\r\nAnother diagnostic line\r\nPartial";
            Layout(window);
            Preserved();
            Assert.Equal(2, view.UnreadLines);
            Assert.Equal(Visibility.Visible, jump.Visibility);
            wizard.Finish.Progress += " continued";
            Layout(window);
            Preserved();
            Assert.Equal(2, view.UnreadLines);
            foreach (var language in new[] { InstallerLanguage.English, InstallerLanguage.Russian })
            {
                var technical = log.Text;
                presentation.ChooseLanguage(language);
                Layout(window);
                Assert.Equal(technical, log.Text);
                Preserved();
                Assert.Contains(PageLabels.JumpToLatestEntries, Peer(jump).GetName(), StringComparison.Ordinal);
                Assert.Contains("2", Peer(jump).GetName(), StringComparison.Ordinal);
            }
            ((IInvokeProvider)Peer(jump).GetPattern(PatternInterface.Invoke)!).Invoke();
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Layout(window);
            AtEnd();
            Assert.Equal(0, view.UnreadLines);
            Assert.Equal(Visibility.Collapsed, jump.Visibility);
            wizard.Finish.Progress += "\r\nFollowing resumed";
            Layout(window);
            AtEnd();
            log.ScrollToHome();
            Layout(window);
            wizard.Finish.Progress += "\r\nUnread again";
            Layout(window);
            Assert.Equal(1, view.UnreadLines);
            log.ScrollToEnd();
            Layout(window);
            Assert.Equal(0, view.UnreadLines);
            wizard.Finish.Progress += "\r\nManual following resumed";
            Layout(window);
            AtEnd();
            wizard.Finish.Progress = "New run";
            Layout(window);
            Assert.Equal("New run", log.Text);
            Assert.Equal(0, view.UnreadLines);

            void Preserved()
            {
                Assert.Equal(offset, log.VerticalOffset, 3);
                Assert.Equal(selected, log.SelectedText);
                Assert.True(ApplicationCommands.Copy.CanExecute(null, log));
            }
            void AtEnd() => Assert.InRange(log.ExtentHeight - log.ViewportHeight - log.VerticalOffset, -1, 1);
        }
    }

    private static void CheckField(Window window, DependencyProperty property, string bindingPath, string expected)
    {
        var control = Descendants(window.Content).OfType<Control>().Single(item =>
            BindingOperations.GetBindingExpression(item, property)?.ParentBinding.Path.Path == bindingPath);
        var label = AutomationProperties.GetLabeledBy(control);
        Assert.NotNull(label);
        Assert.Equal(expected, Peer(control).GetName());
        Assert.Equal(expected, Assert.IsType<TextBlock>(label).Text);
        Assert.Equal(expected, Peer(label).GetName());
    }

    // Help is the explanation already on the page, not a second wording of it.
    private static void CheckHelp(Window window, UIElement control, params string[] explanation)
    {
        var shown = Descendants(window.Content).OfType<TextBlock>().Select(text => text.Text).ToHashSet();
        Assert.All(explanation, part =>
        {
            Assert.False(string.IsNullOrWhiteSpace(part));
            Assert.Contains(part, shown);
        });
        Assert.Equal(string.Join(" ", explanation), Peer(control).GetHelpText());
    }

    private static T Bound<T>(Window window, DependencyProperty property, string path) where T : DependencyObject =>
        Descendants(window.Content).OfType<T>().Single(item =>
            BindingOperations.GetBindingExpression(item, property)?.ParentBinding.Path.Path == path);

    private static IEnumerable<DependencyObject> Ancestors(DependencyObject element)
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            yield return parent;
    }

    // Only selects a presentation fixture: never starts detection, installation or removal.
    private static void ShowPage(Window window, InstallerWizardViewModel wizard, InstallerPage page)
    {
        typeof(InstallerWizardViewModel).GetProperty(nameof(wizard.CurrentPage))!.SetValue(wizard, page);
        Layout(window);
    }

    private static void Layout(Window window)
    {
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(900, 900));
        root.Arrange(new Rect(0, 0, 900, 900));
        root.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
    }

    private static AutomationPeer Peer(UIElement element) => UIElementAutomationPeer.CreatePeerForElement(element)!;

    private static IEnumerable<DependencyObject> Descendants(object parent)
    {
        if (parent is not DependencyObject root) yield break;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
