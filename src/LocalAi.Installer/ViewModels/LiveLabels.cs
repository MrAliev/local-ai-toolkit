using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;
using System.Windows.Data;
using System.Windows.Markup;

namespace LocalAi.Installer.ViewModels;

/// <summary>
/// <see cref="PageLabels"/> for bindings, so a language chosen after a window opened reaches the
/// labels already on screen. Not WPF's binding to a static property: <c>(vm:PageLabels.X)</c>
/// throws inside the binding engine on this runtime.
/// </summary>
public sealed class LiveLabels : INotifyPropertyChanged
{
    private static readonly ConcurrentDictionary<string, PropertyInfo> Properties = new(StringComparer.Ordinal);

    private LiveLabels()
    {
    }

    public static LiveLabels Current { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string name] => (string)Properties
        .GetOrAdd(name, static key => typeof(PageLabels).GetProperty(
            key,
            BindingFlags.Public | BindingFlags.Static) ??
            throw new ArgumentException($"PageLabels has no label named {key}.", nameof(name)))
        .GetValue(null)!;

    internal void Announce() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Binding.IndexerName));
}

/// <summary><c>{vm:Label Back}</c>: a one-way binding to that label.</summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class LabelExtension(string name) : MarkupExtension
{
    [ConstructorArgument("name")]
    public string Name { get; } = name;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding("[" + Name + "]")
        {
            Source = LiveLabels.Current,
            Mode = BindingMode.OneWay,
        }.ProvideValue(serviceProvider);
}
