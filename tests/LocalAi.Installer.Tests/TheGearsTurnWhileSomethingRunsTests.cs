using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace LocalAi.Installer.Tests;

/// <summary>
/// The busy spinner: two meshed gears beside the line that names the wait. They turn only while
/// they are shown and Windows allows animation, they are the palette's blue in every theme, and
/// they are neither focusable nor read out - the line beside them says what is happening.
/// </summary>
public sealed class TheGearsTurnWhileSomethingRunsTests
{
    [Fact]
    public async Task The_gears_turn_while_shown_and_stop_without_motion_or_when_hidden()
    {
        await WpfTestThread.Run(() =>
        {
            var (panel, gears) = Gears();
            using var surface = Surface(panel);
            Frame();
            var turn = Turn(gears);

            Assert.Equal(ClockState.Active, turn.GetCurrentState(gears));
            MotionPreference.SetEnabled(gears, false);
            Frame();
            Assert.Equal(ClockState.Stopped, turn.GetCurrentState(gears));
            MotionPreference.SetEnabled(gears, true);
            Frame();
            Assert.Equal(ClockState.Active, turn.GetCurrentState(gears));
            gears.Visibility = Visibility.Collapsed;
            Frame();
            Assert.Equal(ClockState.Stopped, turn.GetCurrentState(gears));
        });
    }

    /// <summary>The owner's reference: the large gear turns anticlockwise, the small one clockwise.</summary>
    [Fact]
    public async Task The_large_gear_turns_anticlockwise_and_the_small_one_clockwise()
    {
        await WpfTestThread.Run(() =>
        {
            var (panel, gears) = Gears();
            using var surface = Surface(panel);
            Frame();
            var turns = Turn(gears).Children.OfType<DoubleAnimation>()
                .ToDictionary(animation => Storyboard.GetTargetName(animation), animation => animation.To);

            Assert.Equal(-360, turns["Large"]);
            Assert.Equal(360, turns["Small"]);
        });
    }

    /// <summary>The owner chose blue for both gears: it reads on a light page and a dark one alike.</summary>
    [Fact]
    public async Task Both_gears_are_the_palettes_blue()
    {
        await WpfTestThread.Run(() =>
        {
            var (panel, gears) = Gears();
            using var surface = Surface(panel);
            Frame();
            var accent = (SolidColorBrush)panel.FindResource("Accent");

            foreach (var name in new[] { "Large", "Small" })
            {
                var fill = (SolidColorBrush)((Shape)gears.Template.FindName(name, gears)).Fill;
                Assert.Equal(accent.Color, fill.Color);
            }
        });
    }

    /// <summary>A wait shorter than this never shows the gears, so a quick check does not flicker.</summary>
    [Fact]
    public async Task The_gears_appear_only_after_the_wait_has_lasted_a_moment()
    {
        await WpfTestThread.Run(() =>
        {
            var (panel, gears) = Gears();
            using var surface = Surface(panel);
            Frame();
            var appear = gears.Template.Triggers.OfType<Trigger>()
                .Single(trigger => trigger.Property == UIElement.IsVisibleProperty);
            var frame = ((BeginStoryboard)appear.EnterActions[0]).Storyboard.Children
                .OfType<DoubleAnimationUsingKeyFrames>().Single().KeyFrames.Cast<DoubleKeyFrame>().Single();

            Assert.Equal(TimeSpan.FromSeconds(0.4), frame.KeyTime.TimeSpan);
            Assert.Equal(1, frame.Value);
        });
    }

    [Fact]
    public async Task The_gears_take_no_focus_and_are_not_read_out()
    {
        await WpfTestThread.Run(() =>
        {
            var (panel, gears) = Gears();
            using var surface = Surface(panel);
            Frame();

            Assert.False(gears.Focusable);
            Assert.False(gears.IsTabStop);
            Assert.Null(UIElementAutomationPeer.CreatePeerForElement(gears));
        });
    }

    private static (StackPanel Panel, Control Gears) Gears()
    {
        var panel = new StackPanel();
        foreach (var name in new[] { "Light", "Controls" })
        {
            panel.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"/LocalAi.Installer;component/Themes/{name}.xaml", UriKind.Relative),
            });
        }

        var gears = new Control { Style = (Style)panel.FindResource("BusyGears") };
        MotionPreference.SetEnabled(gears, true);
        panel.Children.Add(gears);
        return (panel, gears);
    }

    private static Storyboard Turn(Control gears) =>
        ((BeginStoryboard)gears.Template.Triggers.OfType<MultiTrigger>().Single().EnterActions[0]).Storyboard;

    private static HwndSource Surface(Visual root) =>
        new(new HwndSourceParameters("Busy gears test")
        {
            WindowStyle = unchecked((int)0x80000000), Width = 240, Height = 100,
        }) { RootVisual = root };

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
