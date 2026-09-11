using System.Xml.Linq;

namespace LocalAi.Installer.Tests;

/// <summary>
/// The system check reads as a table and was not one. Every row got its own <c>Grid</c>, so
/// <c>Auto</c> sized each row separately: «Свободное место на диске» is wider than the 150 floor
/// and «Сеть» is not, and that row's OK stepped right of the eight above it.
///
/// A column is only a column if every row agrees where it starts, which is what a shared size
/// scope is for. The floors stay, smaller, as what a run of short labels falls back to — they are
/// no longer what aligns anything.
/// </summary>
public sealed class TheCheckTableLinesUpTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void Every_row_agrees_where_its_columns_start()
    {
        var checks = Checks();

        Assert.Equal("True", checks.Attribute("Grid.IsSharedSizeScope")?.Value);

        var columns = checks.Descendants(Presentation + "ColumnDefinition").ToList();
        Assert.Equal(3, columns.Count);
        // The last one takes what is left, so it has nothing to agree about.
        Assert.All(
            columns.Take(2),
            column => Assert.NotNull(column.Attribute("SharedSizeGroup")?.Value));
    }

    private static XElement Checks() =>
        XDocument.Parse(InstallerMarkup.Read("MainWindow.xaml"))
            .Descendants(Presentation + "ItemsControl")
            .Single(control => control.Attribute("ItemsSource")?.Value
                .Contains("Diagnose.Checks", StringComparison.Ordinal) == true);
}
