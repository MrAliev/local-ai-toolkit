using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace LocalAi.Installer.Tests;

public sealed class TheWaitingBarMovesTests
{
    [Fact]
    public async Task Waiting_stays_visible_without_motion_and_stops_animating_when_complete()
    {
        await WpfTestThread.Run(() =>
        {
            var panel = new StackPanel();
            foreach (var name in new[] { "Light", "Controls" })
                panel.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri($"/LocalAi.Installer;component/Themes/{name}.xaml", UriKind.Relative),
            });
        var bar = new ProgressBar { IsIndeterminate = true, Width = 200 };
        MotionPreference.SetEnabled(bar, true);
        panel.Children.Add(bar);
        using var surface = new HwndSource(new HwndSourceParameters("Waiting animation test")
        {
            WindowStyle = unchecked((int)0x80000000), Width = 240, Height = 100,
        }) { RootVisual = panel };
        Frame();
        var waiting = bar.Template.Triggers.OfType<MultiTrigger>().Single();
        var pulse = ((BeginStoryboard)waiting.EnterActions[0]).Storyboard;
        Assert.Equal(ClockState.Active, pulse.GetCurrentState(bar));
        MotionPreference.SetEnabled(bar, false);
        Frame();
        Assert.Equal(ClockState.Stopped, pulse.GetCurrentState(bar));
        var indicator = (FrameworkElement)bar.Template.FindName("PART_Indicator", bar);
        Assert.True(indicator.ActualWidth > 0);
        Assert.Equal(1, indicator.Opacity);
        MotionPreference.SetEnabled(bar, true);
        Frame();
        Assert.Equal(ClockState.Active, pulse.GetCurrentState(bar));
        bar.IsIndeterminate = false;
        Frame();
        Assert.Equal(ClockState.Stopped, pulse.GetCurrentState(bar));
        });
    }

    private static void Frame()
    {
        var frame = new DispatcherFrame();
        EventHandler rendered = (_, _) => Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        CompositionTarget.Rendering += rendered;
        try { Dispatcher.PushFrame(frame); }
        finally { CompositionTarget.Rendering -= rendered; }
    }
}
