using System.Windows;
using System.Windows.Media;

namespace LocalAi.Installer.ViewModels;

public static class HighContrastPalette
{
    public static ResourceDictionary Create()
    {
        var palette = new ResourceDictionary();
        Add(SystemColors.WindowBrush, "WindowFill", "RailFill", "Surface", "ControlFill", "WarnFill", "ErrFill", "ScrollTrack", "RaisedEdge");
        Add(SystemColors.WindowTextBrush, "TextPrimary", "TextSecondary", "TextMuted", "ControlBorder", "Divider",
            "WarnText", "WarnLine", "ErrText", "ErrLine", "ScrollThumb", "Success");
        Add(SystemColors.GrayTextBrush, "TextDisabled");
        Add(SystemColors.HighlightBrush, "Accent", "AccentFill", "FocusRing", "SelectionFill", "ScrollThumbHover");
        Add(SystemColors.HighlightTextBrush, "AccentForeground");
        return palette;

        void Add(Brush brush, params string[] names)
        {
            foreach (var name in names) palette.Add(name, brush);
        }
    }
}
