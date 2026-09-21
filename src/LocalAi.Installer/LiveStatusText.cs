using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Threading;

namespace LocalAi.Installer;

/// <summary>A visible WPF live region. It never moves keyboard focus.</summary>
public class LiveStatusText : TextBlock
{
    // Null announces Text. Empty suppresses transient updates such as download progress.
    public static readonly DependencyProperty AnnouncementProperty = DependencyProperty.Register(
        nameof(Announcement), typeof(string), typeof(LiveStatusText), new PropertyMetadata(null, OnMessageChanged));
    private string? lastAnnouncement;
    private bool queued;

    static LiveStatusText() => TextProperty.OverrideMetadata(typeof(LiveStatusText),
        new FrameworkPropertyMetadata(string.Empty, OnMessageChanged));

    public string? Announcement
    {
        get => (string?)GetValue(AnnouncementProperty);
        set => SetValue(AnnouncementProperty, value);
    }

    public LiveStatusText()
    {
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);
        Loaded += (_, _) => QueueAnnouncement();
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) lastAnnouncement = null;
            else QueueAnnouncement();
        };
        Unloaded += (_, _) => lastAnnouncement = null;
    }

    private static void OnMessageChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
        ((LiveStatusText)sender).QueueAnnouncement();

    private void QueueAnnouncement()
    {
        if (queued || !IsLoaded || !IsVisible || Dispatcher.HasShutdownStarted) return;
        queued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            queued = false;
            if (!IsLoaded || !IsVisible) return;
            var message = Announcement ?? Text;
            if (string.IsNullOrWhiteSpace(message))
            {
                lastAnnouncement = null;
                ClearValue(AutomationProperties.NameProperty);
                return;
            }
            if (message == lastAnnouncement) return;
            lastAnnouncement = message;
            // Bindings have updated the visible text before the live event is raised.
            AutomationProperties.SetName(this, message);
            var peer = UIElementAutomationPeer.FromElement(this) ?? UIElementAutomationPeer.CreatePeerForElement(this);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }));
    }
}
