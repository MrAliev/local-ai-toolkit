using System.Globalization;
using System.Windows.Data;

namespace LocalAi.Installer;

/// <summary>
/// Gives every line an object of its own. A list's automation peers are keyed by its items, so a
/// line repeated under a second heading was one item to a screen reader and was announced once.
/// </summary>
public sealed class DistinctLinesConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is IEnumerable<string> lines ? lines.Select(line => new Line(line)).ToArray() : value;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    /// <summary>Compared by reference; its text is what the list item is called.</summary>
    public sealed class Line(string text)
    {
        public string Text { get; } = text;

        public override string ToString() => Text;
    }
}
