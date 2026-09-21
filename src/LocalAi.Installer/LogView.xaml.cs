using System.Windows;
using System.Windows.Controls;

namespace LocalAi.Installer;

/// <summary>A selectable log that follows new entries until the reader scrolls into history.</summary>
public partial class LogView : UserControl
{
    public static readonly DependencyProperty LogTextProperty = DependencyProperty.Register(
        nameof(LogText), typeof(string), typeof(LogView), new PropertyMetadata(null,
            (sender, args) => ((LogView)sender).UpdateLog((string?)args.NewValue ?? string.Empty)));
    private static readonly DependencyPropertyKey UnreadLinesPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(UnreadLines), typeof(int), typeof(LogView), new PropertyMetadata(0));
    public static readonly DependencyProperty UnreadLinesProperty = UnreadLinesPropertyKey.DependencyProperty;
    private bool following = true;
    private bool updating;

    public LogView() => InitializeComponent();

    public string? LogText
    {
        get => (string?)GetValue(LogTextProperty);
        set => SetValue(LogTextProperty, value);
    }

    public int UnreadLines
    {
        get => (int)GetValue(UnreadLinesProperty);
        private set => SetValue(UnreadLinesPropertyKey, value);
    }

    private void UpdateLog(string text)
    {
        var previous = Entries.Text;
        if (text == previous) return;
        var append = text.StartsWith(previous, StringComparison.Ordinal);
        var vertical = Entries.VerticalOffset;
        var horizontal = Entries.HorizontalOffset;
        var start = Entries.SelectionStart;
        var length = Entries.SelectionLength;
        updating = true;
        try
        {
            if (append)
            {
                Entries.AppendText(text[previous.Length..]);
                Entries.Select(start, length);
                if (!following) UnreadLines += Math.Max(0, LineCount(text) - LineCount(previous));
            }
            else
            {
                // A new run replaces the old document and starts following its own tail.
                Entries.Text = text;
                following = true;
                UnreadLines = 0;
            }
            UpdateJump();
            Entries.UpdateLayout();
            if (following) Entries.ScrollToEnd();
            else Entries.ScrollToVerticalOffset(vertical);
            Entries.ScrollToHorizontalOffset(horizontal);
        }
        finally { updating = false; }
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (updating) return;
        // Text growth and resizing are layout changes, not a request to pause following.
        if (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0 || e.ViewportWidthChange != 0)
        {
            if (following) Entries.ScrollToEnd();
            return;
        }
        if (e.VerticalChange == 0) return;
        following = Entries.ExtentHeight - Entries.ViewportHeight - Entries.VerticalOffset <= 1;
        if (following) UnreadLines = 0;
        UpdateJump();
    }

    private void OnJumpToLatest(object sender, RoutedEventArgs e)
    {
        following = true;
        UnreadLines = 0;
        // The action disappears when following resumes; leave keyboard focus on the log.
        Entries.Focus();
        UpdateJump();
        Entries.ScrollToEnd();
    }

    private void UpdateJump() => JumpButton.Visibility = following ? Visibility.Collapsed : Visibility.Visible;

    private static int LineCount(string text) => text.Length == 0 ? 0
        : text.Count(character => character == '\n') + (text[^1] == '\n' ? 0 : 1);
}
