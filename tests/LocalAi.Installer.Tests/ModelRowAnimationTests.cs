using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Xml.Linq;
using LocalAi.Installer.Core.Models;
using LocalAi.Installer.ViewModels;
using ShapePath = System.Windows.Shapes.Path;

namespace LocalAi.Installer.Tests;

public sealed class ModelRowAnimationTests
{
    [Theory]
    [InlineData(ModelProvisioningPhase.Accepted, RunMark.Done, false)]
    [InlineData(ModelProvisioningPhase.Rejected, RunMark.Warned, false)]
    [InlineData(ModelProvisioningPhase.Accepted, RunMark.Warned, true)]
    public async Task The_shipped_row_style_stops_only_the_completed_models_animation(
        ModelProvisioningPhase verdict,
        RunMark expectedMark,
        bool residencyDegraded)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                AssertAnimation(verdict, expectedMark, residencyDegraded);
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

    private static void AssertAnimation(
        ModelProvisioningPhase verdict, RunMark expectedMark, bool residencyDegraded)
    {
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var styleMarkup = XDocument.Parse(InstallerMarkup.Read("MainWindow.xaml"))
            .Descendants(presentation + "Style")
            .Single(style => (string?)style.Attribute(xaml + "Key") == "RowMark");
        var dictionaryMarkup = new XElement(presentation + "ResourceDictionary",
            new XAttribute(XNamespace.Xmlns + "x", xaml),
            new XAttribute(XNamespace.Xmlns + "vm",
                "clr-namespace:LocalAi.Installer.ViewModels;assembly=LocalAi.Installer"),
            new XElement(presentation + "ResourceDictionary.MergedDictionaries",
                new XElement(presentation + "ResourceDictionary", new XAttribute("Source",
                    "/LocalAi.Installer;component/Themes/Light.xaml")),
                new XElement(presentation + "ResourceDictionary", new XAttribute("Source",
                    "/LocalAi.Installer;component/Themes/Controls.xaml"))),
            new XElement(styleMarkup));
        var resources = (ResourceDictionary)XamlReader.Parse(dictionaryMarkup.ToString());
        var style = (Style)resources["RowMark"];
        var busy = style.Triggers.OfType<DataTrigger>()
            .Single(trigger => Equals(trigger.Value, RunMark.Busy));
        var spinner = ((BeginStoryboard)busy.EnterActions[0]).Storyboard;
        var wizard = new InstallerWizardViewModel();
        wizard.ApplyModelStep(Step("first", ModelProvisioningPhase.Planned));
        wizard.ApplyModelStep(Step("second", ModelProvisioningPhase.Planned));
        var panel = new StackPanel();
        panel.Resources.MergedDictionaries.Add(resources);
        var first = Mark(wizard.ModelRuns[0]);
        var second = Mark(wizard.ModelRuns[1]);
        panel.Children.Add(first);
        panel.Children.Add(second);
        // A hidden native surface loads the real WPF style without opening the installer or
        // running its environment probes. No elapsed-time assertion is involved.
        using var surface = new HwndSource(new HwndSourceParameters("Model row animation test")
        {
            WindowStyle = unchecked((int)0x80000000),
            Width = 240,
            Height = 100,
        }) { RootVisual = panel };
        DrainDispatcher();
        wizard.ApplyModelStep(Step("first", ModelProvisioningPhase.Checking));
        wizard.ApplyModelStep(Step("second", ModelProvisioningPhase.Checking));
        DrainDispatcher();
        Assert.Equal(ClockState.Active, spinner.GetCurrentState(first));
        Assert.Equal(ClockState.Active, spinner.GetCurrentState(second));

        wizard.ApplyModelStep(Step("first", verdict) with { ResidencyDegraded = residencyDegraded });
        DrainDispatcher();

        Assert.Equal(expectedMark, wizard.ModelRuns[0].Mark);
        Assert.Equal(ClockState.Stopped, spinner.GetCurrentState(first));
        Assert.Equal(ClockState.Active, spinner.GetCurrentState(second));

        ShapePath Mark(ModelRunRow row) => new()
        {
            DataContext = row,
            Style = style,
            RenderTransform = new RotateTransform(0, 5.5, 5.5),
        };
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static ModelProvisioningProgress Step(string model, ModelProvisioningPhase phase) =>
        new(model, 2048, phase, 0, 2);
}
