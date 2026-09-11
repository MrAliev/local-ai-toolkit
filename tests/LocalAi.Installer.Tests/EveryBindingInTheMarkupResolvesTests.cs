using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using LocalAi.Installer.ViewModels;

namespace LocalAi.Installer.Tests;

/// <summary>
/// Every binding path in the markup names a member that exists, and every resource key it uses is
/// declared in the same file.
///
/// A broken WPF binding is silent. It writes a line to a debug listener nobody reads, leaves the
/// control blank, and passes every test in this suite — so a renamed view-model property takes a
/// checkbox out of the installer and nothing turns red. This repository has no WPF harness and
/// starting a `Window` in a headless test run is not worth what it would cost, but the failure
/// worth catching does not need one: it is a name that no longer resolves.
///
/// So the markup is read as XML and each segment of each path is resolved by reflection against
/// the type it would bind to. What this cannot see is a wrong `RelativeSource`, a `DataContext`
/// that is not what the path assumes, or a converter that mishandles its input — those still need
/// a person. What it does see is the whole of the failure class that follows a rename.
/// </summary>
public sealed class EveryBindingInTheMarkupResolvesTests
{
    private const string Markup = "MainWindow.xaml";

    /// <summary>
    /// Paths that start from the window's own DataContext, which is the wizard. A path reached
    /// through `RelativeSource AncestorType=Window` is written `DataContext.X.Y` and starts there
    /// too, one segment further in.
    /// </summary>
    [Fact]
    public void Every_binding_path_from_the_window_resolves_to_a_real_member()
    {
        var unresolved = new List<string>();
        foreach (var (path, twoWay) in Bindings())
        {
            var segments = path.Split('.');
            var type = typeof(InstallerWizardViewModel);
            for (var index = 0; index < segments.Length; index++)
            {
                var property = Property(type, segments[index]);
                if (property is null)
                {
                    unresolved.Add($"{path} — '{segments[index]}' is not on {type.Name}");
                    break;
                }

                if (index == segments.Length - 1 && twoWay && property.SetMethod is not { IsPublic: true })
                {
                    unresolved.Add($"{path} — bound TwoWay but has no public setter");
                    break;
                }

                type = property.PropertyType;
            }
        }

        Assert.Empty(unresolved);
    }

    /// <summary>
    /// A `{StaticResource X}` whose key is not declared throws at load time rather than failing
    /// quietly, which makes it the one binding-adjacent mistake that is loud — on a machine that
    /// can start the window. This suite never starts one, so it is checked here instead.
    ///
    /// Keys are looked for in the window and in the dictionaries merged into the application,
    /// because that is where they are: the first version of this looked only at the window and
    /// reported `MarkInk`, which lives in `Themes/Brand.xaml` and is perfectly well resolved at
    /// run time.
    /// </summary>
    [Fact]
    public void Every_static_resource_key_is_declared_somewhere_the_window_can_see()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var declared = Dictionaries()
            .SelectMany(text => XDocument.Parse(text).Descendants())
            .Select(element => element.Attribute(x + "Key")?.Value)
            .Where(key => key is not null)
            .ToHashSet(StringComparer.Ordinal);

        var used = Regex
            .Matches(Source(), @"\{StaticResource\s+(?<key>[A-Za-z0-9_.]+)\s*\}")
            .Select(match => match.Groups["key"].Value)
            // A style keyed by type — `{StaticResource {x:Type TextBlock}}` — does not match the
            // pattern above, so what is left is only the named ones.
            .Where(key => !declared.Contains(key))
            .ToList();

        Assert.Empty(used);
    }

    /// <summary>
    /// Bindings written against the item of an `ItemsControl` rather than the window. They are
    /// resolved against the row type, which is the only thing those templates are ever given.
    /// </summary>
    [Fact]
    public void Every_binding_path_from_a_client_row_resolves_to_a_real_member()
    {
        var missing = RowBindings()
            .Where(path => Property(typeof(AgentOption), path) is null)
            .ToList();

        Assert.Empty(missing);
    }

    /// <summary>
    /// The prerequisites rows, resolved against the record they are handed. Their head line is
    /// assembled in the view model rather than out of &lt;Run&gt;s, so a rename there takes the
    /// name of a component off the page and nothing else says so.
    /// </summary>
    [Fact]
    public void Every_binding_path_from_a_prerequisite_row_resolves_to_a_real_member()
    {
        var missing = DependencyRowBindings()
            .Where(path => Property(typeof(DependencySelection), path) is null)
            .ToList();

        Assert.Empty(missing);
    }

    private static PropertyInfo? Property(Type type, string name) =>
        type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);

    /// <summary>
    /// Every `{Binding DataContext.…}`, with whether it was declared TwoWay. The `DataContext.`
    /// prefix is what marks a path as starting from the window rather than from whatever the
    /// nearest template is handing out, so it is the set this can resolve without knowing which
    /// template a control sits in.
    /// </summary>
    private static IEnumerable<(string Path, bool TwoWay)> Bindings() =>
        Regex.Matches(Source(), @"\{Binding\s+DataContext\.(?<path>[A-Za-z0-9_.]+)(?<rest>[^}]*)\}")
            .Select(match => (
                match.Groups["path"].Value,
                match.Groups["rest"].Value.Contains("Mode=TwoWay", StringComparison.Ordinal)));

    /// <summary>
    /// The paths bound inside the client-applications template, named explicitly rather than
    /// scraped: the file has several templates over several item types, and guessing which row a
    /// path belongs to from the markup alone is how a check of this kind starts lying.
    /// </summary>
    private static string[] RowBindings() =>
        ["Heading", "Agent", "Choice", "HasTextSearchGate", "DisplayName", "DetectionText"];

    private static string[] DependencyRowBindings() =>
        ["Id", "IsConsented", "IsInstallable", "HeadLine", "RequirementText"];

    private static string Source() => InstallerMarkup.Read(Markup);

    /// <summary>
    /// The window plus everything the application merges in, which is where a key it uses may
    /// legitimately live.
    /// </summary>
    private static IEnumerable<string> Dictionaries()
    {
        yield return Source();
        yield return InstallerMarkup.Read("App.xaml");
        var themes = Path.Combine(InstallerMarkup.Directory, "Themes");
        foreach (var dictionary in Directory.EnumerateFiles(themes, "*.xaml"))
        {
            yield return File.ReadAllText(dictionary);
        }
    }
}
