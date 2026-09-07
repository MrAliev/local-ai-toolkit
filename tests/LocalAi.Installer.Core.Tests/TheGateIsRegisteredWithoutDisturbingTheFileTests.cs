using LocalAi.Installer.Core.Agents;

namespace LocalAi.Installer.Core.Tests;

/// <summary>
/// `~/.claude/settings.json` is the fourth file this product writes into somebody else's
/// configuration, and the first that is a JSON document a person edits by hand: it carries their
/// theme, their permissions, their plugins and — the part that makes this delicate — their own
/// hooks, possibly on the very event LocalAi wants (#423).
///
/// So the contract is narrow. Add ours and change nothing else; take ours back out and leave the
/// file as it was, byte for byte. The second half is the one worth pinning hardest, because a
/// removal that reformats the document is indistinguishable from a removal that lost something.
/// </summary>
public sealed class TheGateIsRegisteredWithoutDisturbingTheFileTests
{
    private const string Command =
        "\"C:\\LocalAi\\bin\\launcher\\localai-launcher.exe\" run localai agent gate";

    [Fact]
    public void A_machine_with_no_settings_file_gets_one_that_is_only_the_hook()
    {
        var after = ClaudeHookRegistration.Add(string.Empty, Command);

        Assert.True(ClaudeHookRegistration.IsRegistered(after, Command));
        Assert.Contains("PreToolUse", after, StringComparison.Ordinal);
        // Through the parsed document rather than by looking for the command in the text: the
        // path is full of backslashes and quotes, JSON escapes both, and an assertion that
        // searched the raw text for it would be looking for a string that cannot be there.
        Assert.Contains("Bash|PowerShell", after, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_that_carry_no_hooks_keep_every_byte_they_had()
    {
        const string before = "{\n  \"theme\": \"dark\",\n  \"editorMode\": \"vim\"\n}";

        var after = ClaudeHookRegistration.Add(before, Command);

        Assert.Contains("  \"theme\": \"dark\"", after, StringComparison.Ordinal);
        Assert.Contains("  \"editorMode\": \"vim\"", after, StringComparison.Ordinal);
        Assert.True(ClaudeHookRegistration.IsRegistered(after, Command));
    }

    /// <summary>
    /// Somebody else's hook on the same event is the case this is written for. It is not ours to
    /// touch, and it has to still be there and still be theirs afterwards.
    /// </summary>
    [Fact]
    public void A_hook_the_user_wrote_on_the_same_event_survives()
    {
        const string before = """
            {
              "hooks": {
                "PreToolUse": [
                  {
                    "matcher": "Write|Edit",
                    "hooks": [ { "type": "command", "command": "prettier --write" } ]
                  }
                ]
              }
            }
            """;

        var after = ClaudeHookRegistration.Add(before, Command);

        Assert.Contains("prettier --write", after, StringComparison.Ordinal);
        Assert.Contains("Write|Edit", after, StringComparison.Ordinal);
        Assert.True(ClaudeHookRegistration.IsRegistered(after, Command));
    }

    [Fact]
    public void Registering_twice_changes_nothing_the_second_time()
    {
        var once = ClaudeHookRegistration.Add("{}", Command);

        var twice = ClaudeHookRegistration.Add(once, Command);

        Assert.Equal(once, twice, StringComparer.Ordinal);
    }

    /// <summary>
    /// The launcher path moves with an installation, so the command in the file can be one this
    /// product wrote at another location. It is still ours, and it is replaced rather than joined
    /// by a second copy that would refuse every call twice.
    /// </summary>
    [Fact]
    public void An_entry_this_product_wrote_at_another_path_is_replaced_rather_than_doubled()
    {
        const string old = "\"D:\\Old\\localai-launcher.exe\" run localai agent gate";
        var before = ClaudeHookRegistration.Add("{}", old);

        var after = ClaudeHookRegistration.Add(before, Command);

        Assert.True(ClaudeHookRegistration.IsRegistered(after, Command));
        // Asked of the parsed document. `Assert.DoesNotContain(old, after)` looks right and
        // passes for the wrong reason: JSON escapes the quotes and backslashes in that path, so
        // the raw string is never in the text whether the old entry is still there or not.
        Assert.False(ClaudeHookRegistration.IsRegistered(after, old));
    }

    /// <summary>
    /// The property this is really about: what an uninstall leaves behind is what was there
    /// before the install. `Removing_the_block_undoes_adding_it` pins the same thing for the
    /// markdown block, and it is the shape that makes an uninstall safe to run.
    /// </summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("{\n  \"theme\": \"dark\"\n}")]
    [InlineData("""
        {
          "theme": "dark",
          "hooks": {
            "PreToolUse": [
              {
                "matcher": "Write|Edit",
                "hooks": [ { "type": "command", "command": "prettier --write" } ]
              }
            ]
          },
          "tail": [1, 2, 3]
        }
        """)]
    public void Removing_the_registration_undoes_adding_it(string before)
    {
        var after = ClaudeHookRegistration.Add(before, Command);

        var removed = ClaudeHookRegistration.Remove(after);

        Assert.Equal(before, removed, StringComparer.Ordinal);
        Assert.False(ClaudeHookRegistration.IsRegistered(removed, Command));
    }

    /// <summary>
    /// Where there was no settings file, removal leaves an empty document rather than deleting
    /// one. Deleting is the tempting answer and it is wrong: this code cannot know whether the
    /// file was ours to create — a person may have written one between the install and the
    /// uninstall — and an uninstall that removes somebody's settings because it once created a
    /// file at that path is a worse outcome than a stray `{}`.
    /// </summary>
    [Fact]
    public void Where_there_was_no_file_removal_leaves_an_empty_document()
    {
        var after = ClaudeHookRegistration.Add(string.Empty, Command);

        Assert.Equal("{}", ClaudeHookRegistration.Remove(after), StringComparer.Ordinal);
    }

    [Fact]
    public void Removing_what_was_never_registered_changes_nothing()
    {
        const string before = "{\n  \"theme\": \"dark\"\n}";

        Assert.Equal(before, ClaudeHookRegistration.Remove(before), StringComparer.Ordinal);
    }

    /// <summary>
    /// A file that is not JSON at all is not quietly replaced with one that is: an uninstall or an
    /// install that overwrote somebody's settings because it could not read them would be the
    /// worst outcome available here.
    /// </summary>
    [Theory]
    [InlineData("not json")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{\"hooks\": 5}")]
    public void A_document_this_cannot_understand_is_refused_rather_than_rewritten(string before)
    {
        Assert.Throws<InvalidOperationException>(() => ClaudeHookRegistration.Add(before, Command));
        Assert.Throws<InvalidOperationException>(() => ClaudeHookRegistration.Remove(before));
    }
}
