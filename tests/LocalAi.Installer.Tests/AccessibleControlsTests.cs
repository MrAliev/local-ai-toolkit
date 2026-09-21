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
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                foreach (var resource in new[] { "Light", "Controls", "Brand" })
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri($"/LocalAi.Installer;component/Themes/{resource}.xaml", UriKind.Relative),
                    });
                try { CheckWindows(); }
                finally { app.Shutdown(); }
                finished.SetResult();
            }
            catch (Exception exception) { finished.SetException(exception); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
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
                    ShowPage(window, wizard, InstallerPage.Confirm);
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
                Assert.False(removalWizard.RemoveSigningKeys);
                Assert.False(removalWizard.IsConfirmed);
            }
        }
        finally { removal.Close(); }
        CheckStartKeyboard(presentation);
        CheckRemovalDefault(presentation, machine);
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
