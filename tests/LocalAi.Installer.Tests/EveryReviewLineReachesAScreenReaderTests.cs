using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Threading;
using LocalAi.Installer.ViewModels;

namespace LocalAi.Installer.Tests;

public sealed class EveryReviewLineReachesAScreenReaderTests
{
    /// <summary>
    /// Two clients already in the form this version writes each get the same line under them, and
    /// the list gave a screen reader only the first: the reinstall's review in RC31 sandbox run
    /// 20260928-153719-clean-reinstall (07-install-confirm.uia.txt) had it under Codex and nothing
    /// under Claude Code.
    /// </summary>
    [Fact]
    public async Task A_line_that_repeats_is_there_each_time()
    {
        const string same = "    already in the form this version writes; nothing to write";
        await WpfTestThread.Run(() =>
        {
            var overview = new EffectsOverview
            {
                DataContext = new[]
                {
                    new ReviewGroup("Clients and PATH", ["Clients:", "  Codex — Register", same, "  Claude Code — Register", same]),
                },
            };
            overview.Measure(new Size(800, 600));
            overview.Arrange(new Rect(0, 0, 800, 600));
            overview.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);

            var lines = PeerTree(UIElementAutomationPeer.CreatePeerForElement(overview)!)
                .Where(peer => peer.GetAutomationControlType() == AutomationControlType.Edit)
                .Select(peer => peer.GetName())
                .ToArray();

            Assert.Equal(["Clients:", "  Codex — Register", same, "  Claude Code — Register", same], lines);
        });
    }

    private static IEnumerable<AutomationPeer> PeerTree(AutomationPeer root)
    {
        yield return root;
        foreach (var child in root.GetChildren() ?? [])
        foreach (var nested in PeerTree(child))
            yield return nested;
    }
}
