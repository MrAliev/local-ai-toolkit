using LocalAi.Installer.ViewModels;

namespace LocalAi.Installer.Tests;

/// <summary>
/// The clients page showed this, in the box where a person picks what happens to Claude and
/// Codex:
///
///     AgentChoiceOption { Choice = McpAndInstructions, Title = Зарегистрировать MCP-серверы и инструкции }
///
/// A closed ComboBox draws `SelectionBoxItemTemplate`, which the control computes from
/// `ItemTemplate` and never from `DisplayMemberPath`; with the path alone the content presenter
/// falls back to `ToString`, and a positional record prints its members.
///
/// The template in `MainWindow.xaml` is the fix. This is the floor under it — a binding that
/// breaks again should degrade to the right words.
/// </summary>
public sealed class TheChoiceReadsAsWordsTests
{
    [Theory]
    [InlineData(AgentChoice.McpAndInstructions)]
    [InlineData(AgentChoice.McpOnly)]
    [InlineData(AgentChoice.InstructionsOnly)]
    [InlineData(AgentChoice.NoChange)]
    public void An_option_prints_its_title_rather_than_its_members(AgentChoice choice)
    {
        var option = new AgentChoiceOption(choice);

        Assert.Equal(choice.Title(), option.ToString(), StringComparer.Ordinal);
        Assert.DoesNotContain("AgentChoiceOption", option.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Choice =", option.ToString(), StringComparison.Ordinal);
    }

    /// <summary>And every one of them says something, in either language.</summary>
    [Fact]
    public void Every_choice_has_a_title_in_both_languages()
    {
        foreach (var choice in Enum.GetValues<AgentChoice>())
        {
            Assert.False(
                string.IsNullOrWhiteSpace(new AgentChoiceOption(choice).ToString()),
                $"{choice} has no title, so its row would be an empty box");
        }
    }
}
