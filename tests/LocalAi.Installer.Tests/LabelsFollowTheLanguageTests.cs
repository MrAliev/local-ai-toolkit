using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using LocalAi.Installer.Core;
using LocalAi.Installer.ViewModels;
using LocalAi.Tests.Shared;

namespace LocalAi.Installer.Tests;

/// <summary>
/// The labels written into the markup follow a language chosen after the window opened (#602).
///
/// They were <c>{x:Static}</c>, which is read once when the window loads — correct while the
/// language could only be chosen before either wizard existed, and wrong the moment it can be
/// chosen on any screen. <c>{vm:Label}</c> is a binding, which reads them again when told to.
///
/// A binding is also quieter than what it replaces: a misspelt <c>x:Static</c> fails the build,
/// a misspelt label name leaves an empty control and a line in a debug listener. So every name is
/// resolved here instead.
/// </summary>
public sealed class LabelsFollowTheLanguageTests
{
    private static readonly string[] Markup =
        ["MainWindow.xaml", "UninstallWindow.xaml", "StartWindow.xaml", "PreferencesBar.xaml"];

    private static readonly Regex LabelPath = new(
        @"\{vm:Label\s+(?<name>[A-Za-z0-9_]+)\s*\}",
        RegexOptions.Compiled);

    [Theory]
    [InlineData("MainWindow.xaml")]
    [InlineData("UninstallWindow.xaml")]
    [InlineData("StartWindow.xaml")]
    [InlineData("PreferencesBar.xaml")]
    public void No_label_is_read_only_once(string file)
    {
        var frozen = File.ReadLines(Path.Combine(InstallerMarkup.Directory, file))
            .Select((line, index) => (line, number: index + 1))
            .Where(entry => entry.line.Contains("{x:Static vm:PageLabels.", StringComparison.Ordinal))
            .Select(entry => $"{file}:{entry.number}  {entry.line.Trim()}")
            .ToList();

        Assert.True(
            frozen.Count == 0,
            "Labels read once, when the window loaded:" + Environment.NewLine +
            string.Join(Environment.NewLine, frozen));
    }

    [Fact]
    public void Every_label_the_markup_binds_to_exists()
    {
        var missing = Markup
            .SelectMany(file => LabelPath.Matches(InstallerMarkup.Read(file))
                .Select(match => (file, name: match.Groups["name"].Value)))
            .Where(label => typeof(PageLabels).GetProperty(
                label.name,
                BindingFlags.Public | BindingFlags.Static)?.PropertyType != typeof(string))
            .Select(label => $"{label.file}: {label.name}")
            .ToList();

        Assert.Empty(missing);
    }

    /// <summary>
    /// The mechanism itself, through the real binding engine: the form the markup uses picks the
    /// new language up without the element being rebuilt.
    /// </summary>
    [Fact]
    public async Task A_bound_label_changes_with_the_language()
    {
        using var root = new TemporaryDirectory("LocalAi-label-binding");
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var presentation = new InstallerPresentation(
                    new InstallerPreferencesStore(root.Combine("preferences")));
                var label = (TextBlock)XamlReader.Parse(
                    """
                    <TextBlock xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                               xmlns:vm="clr-namespace:LocalAi.Installer.ViewModels;assembly=LocalAi.Installer"
                               Text="{vm:Label Back}" />
                    """);
                label.DataContext = new object();
                // A template defers the extension to each instance it builds; the prerequisite and
                // client rows use labels that way.
                var templated = (ContentControl)XamlReader.Parse(
                    """
                    <ContentControl xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                    xmlns:vm="clr-namespace:LocalAi.Installer.ViewModels;assembly=LocalAi.Installer">
                        <ContentControl.ContentTemplate>
                            <DataTemplate>
                                <TextBlock Text="{vm:Label Next}" />
                            </DataTemplate>
                        </ContentControl.ContentTemplate>
                    </ContentControl>
                    """);
                templated.Content = new object();
                templated.Measure(new System.Windows.Size(200, 50));
                DrainDispatcher();
                var row = FindText(templated);
                Assert.NotNull(row);
                Assert.Equal("Back", label.Text);
                Assert.Equal("Next", row.Text);

                presentation.ChooseLanguage(InstallerLanguage.Russian);
                DrainDispatcher();

                Assert.Equal("Назад", label.Text);
                Assert.Equal("Далее", row.Text);
                completed.SetResult();
            }
            catch (Exception exception)
            {
                completed.SetException(exception);
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TestContext.Current.CancellationToken);
    }

    private static TextBlock? FindText(System.Windows.DependencyObject parent)
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            var found = child as TextBlock ?? FindText(child);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
