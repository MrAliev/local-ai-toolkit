using LocalAi.Cli;

namespace LocalAi.IntegrationTests;

/// <summary>
/// The product tells an assistant to use the local tools instead of a text search, and an
/// instruction is not enough: the rule was read, agreed to, written down again and broken about a
/// dozen times in one session, because the reach for `grep` happens before the rule is recalled.
/// A `PreToolUse` hook refuses the call before it runs, and recall is not involved (#423).
///
/// What is under test is the judgement, not the plumbing: given the payload Claude Code hands a
/// hook, is this a search over the tree or something that merely mentions one. The cases below are
/// the ones the hand-installed script learned, each of which was a real false positive or a real
/// hole rather than an invented one.
/// </summary>
public sealed class ATextSearchIsRefusedBeforeItRunsTests
{
    [Theory]
    // In command position: the beginning of the line.
    [InlineData("grep -rn \"Publish\" src/")]
    [InlineData("rg --files-with-matches Publish")]
    [InlineData("findstr /s /n Publish *.cs")]
    [InlineData("Select-String -Path src\\*.cs -Pattern Publish")]
    // After something that starts a new command.
    [InlineData("ls src | grep Publish")]
    [InlineData("cd src && grep -n Publish .")]
    [InlineData("cat file; rg Publish")]
    [InlineData("echo $(grep -c Publish file)")]
    // Behind something that runs another command.
    [InlineData("sudo grep -n root /etc/passwd")]
    [InlineData("find . -name '*.cs' | xargs grep -n Publish")]
    // A search after a heredoc has closed is still a search.
    [InlineData("cat <<'EOF' > notes.md\nmentions grep in prose\nEOF\ngrep -n Publish src/")]
    public void A_search_over_the_tree_is_refused(string command) =>
        Assert.True(TextSearchRefusal.Refuses("Bash", command), command);

    [Theory]
    // Searching the history by message is not searching the tree, and no local tool replaces it.
    [InlineData("git log --grep=\"vector block\"")]
    [InlineData("git log --oneline --grep Publish -n 20")]
    // The word as an argument, or as text.
    [InlineData("echo \"use grep instead\"")]
    [InlineData("dotnet test LocalAi.slnx --configuration Release")]
    [InlineData("gh issue view 423 --json body")]
    // The body of a heredoc is data fed to a command, not commands. This one is the hook's own
    // first false positive: the text of the issue about this rule mentions the commands it forbids.
    [InlineData("cat <<'EOF' > issue.md\nDo not run grep or rg over the tree.\nEOF")]
    // A backtick is a substitution in shell and also how prose quotes a command name. The second
    // reading is the common one in this repository's own documents, so it is not a separator here.
    [InlineData("echo \"the rule is about `grep` and its kin\"")]
    public void Anything_that_is_not_a_search_is_allowed(string command) =>
        Assert.False(TextSearchRefusal.Refuses("Bash", command), command);

    /// <summary>
    /// The shell is not the only way to sweep a tree. A subagent asked for `grep`, was refused,
    /// and reached the same result through the built-in search tools — which the rule covers and
    /// the hook did not. These are a search whatever their arguments say, so the call itself is
    /// refused rather than anything inside it.
    /// </summary>
    [Theory]
    [InlineData("Grep")]
    [InlineData("Glob")]
    public void The_built_in_search_tools_are_refused_whatever_they_are_asked_for(string tool) =>
        Assert.True(TextSearchRefusal.Refuses(tool, command: null), tool);

    /// <summary>
    /// `git grep` is a sweep of the tree and got through the hand-installed script, because `git`
    /// is not one of the words that run another command and the search word therefore never stood
    /// at the start of a command. Closing it is the one behaviour this differs from what was
    /// running by hand — and `git log --grep` above proves the close did not take the history
    /// search with it.
    /// </summary>
    [Fact]
    public void A_search_run_through_git_is_refused_too() =>
        Assert.True(TextSearchRefusal.Refuses("Bash", "git grep -n Publish"));

    /// <summary>
    /// Two refusals rather than one, split by what is being searched. The single text this
    /// replaces was false on `Glob`: that tool matches paths and does no string matching at all,
    /// so telling its caller that the local tools "answer by position rather than by string match"
    /// answers a question nobody asked — and sending them to `search_code` for what is on disk
    /// sends them to the wrong tool, since listing the directory answers that and is not refused.
    /// </summary>
    [Fact]
    public void Searching_content_and_matching_paths_are_refused_in_different_words()
    {
        var content = TextSearchRefusal.ReasonFor("Grep", command: null);
        var paths = TextSearchRefusal.ReasonFor("Glob", command: null);

        Assert.Contains("searches this tree for a string", content, StringComparison.Ordinal);
        Assert.Contains("matches file paths", paths, StringComparison.Ordinal);
        Assert.Contains("listing the directory, which is not refused", paths, StringComparison.Ordinal);
        Assert.DoesNotContain("string match", paths, StringComparison.Ordinal);
    }

    /// <summary>
    /// The refusal names what was refused, so it can never name something that was allowed — the
    /// matched command for a shell call, the tool for a built-in one.
    /// </summary>
    [Theory]
    [InlineData("rg Publish", "rg")]
    [InlineData("ls | findstr Publish", "findstr")]
    [InlineData("git grep -n Publish", "grep")]
    public void The_refusal_names_the_command_it_refused(string command, string named) =>
        Assert.Contains($"`{named}`", TextSearchRefusal.ReasonFor("Bash", command), StringComparison.Ordinal);

    /// <summary>
    /// Narrowing the pattern after a refusal is the reflex, and it does not work: the call is
    /// refused whatever its arguments say. Saying so costs a sentence and saves a round trip.
    /// </summary>
    [Fact]
    public void The_refusal_says_that_a_narrower_pattern_will_not_help() =>
        Assert.Contains(
            "refused whatever its arguments say",
            TextSearchRefusal.ReasonFor("Bash", "grep -n Publish src/"),
            StringComparison.Ordinal);

    [Fact]
    public void A_refused_call_answers_with_the_decision_a_hook_is_read_for()
    {
        var decision = TextSearchRefusal.DecisionFor(
            """{"tool_name":"Bash","tool_input":{"command":"grep -rn Publish src/"}}""");

        Assert.NotNull(decision);
        Assert.Contains("\"hookEventName\":\"PreToolUse\"", decision, StringComparison.Ordinal);
        Assert.Contains("\"permissionDecision\":\"deny\"", decision, StringComparison.Ordinal);
    }

    [Fact]
    public void An_allowed_call_answers_with_nothing_at_all() =>
        Assert.Null(TextSearchRefusal.DecisionFor(
            """{"tool_name":"Bash","tool_input":{"command":"dotnet build"}}"""));

    /// <summary>
    /// A hook that cannot read its input must not block work it never inspected. Every one of
    /// these is a payload this command has no business having an opinion about.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{")]
    [InlineData("""{"tool_name":"Bash"}""")]
    [InlineData("""{"tool_input":{"command":"grep x"}""")]
    public void An_unreadable_payload_lets_the_call_through(string payload) =>
        Assert.Null(TextSearchRefusal.DecisionFor(payload));

    /// <summary>
    /// The command answers a refusal on standard output and exits zero. A non-zero code in the
    /// `PreToolUse` contract means the hook itself failed rather than that it refused, so a gate
    /// that exited non-zero would read as broken and be ignored.
    /// </summary>
    [Fact]
    public void The_command_writes_the_refusal_and_still_exits_zero()
    {
        var streams = Captured();

        var code = AgentGateCommand.Execute(
            [],
            streams.Streams,
            new StringReader("""{"tool_name":"Bash","tool_input":{"command":"rg Publish"}}"""),
            inputRedirected: true);

        Assert.Equal(0, code);
        Assert.Contains("\"permissionDecision\":\"deny\"", streams.Out.ToString(), StringComparison.Ordinal);
        Assert.Empty(streams.Error.ToString());
    }

    [Fact]
    public void The_command_says_nothing_at_all_about_a_call_it_allows()
    {
        var streams = Captured();

        var code = AgentGateCommand.Execute(
            [],
            streams.Streams,
            new StringReader("""{"tool_name":"Bash","tool_input":{"command":"dotnet build"}}"""),
            inputRedirected: true);

        Assert.Equal(0, code);
        Assert.Empty(streams.Out.ToString());
        Assert.Empty(streams.Error.ToString());
    }

    /// <summary>
    /// Run by hand at a console it would otherwise wait for input that never comes and look hung.
    /// The sentence is the one thing in this command a person reads, which is why it is the one
    /// thing in it that follows the reader's language.
    /// </summary>
    [Fact]
    public void Run_by_hand_it_says_what_it_is_for_instead_of_waiting()
    {
        var streams = Captured();

        var code = AgentGateCommand.Execute(
            [],
            streams.Streams,
            new StringReader(string.Empty),
            inputRedirected: false);

        Assert.Equal(2, code);
        Assert.Contains("stdin", streams.Error.ToString(), StringComparison.Ordinal);
        Assert.Empty(streams.Out.ToString());
    }

    [Fact]
    public void An_argument_it_does_not_take_is_refused_with_the_usage_line()
    {
        var streams = Captured();

        var code = AgentGateCommand.Execute(
            ["--json"],
            streams.Streams,
            new StringReader(string.Empty),
            inputRedirected: true);

        Assert.Equal(2, code);
        Assert.Contains("--json", streams.Error.ToString(), StringComparison.Ordinal);
        Assert.Contains(CliUsage.AgentGate, streams.Error.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// What this command prints is the client's own schema, not a MachineEnvelope, so it must stay
    /// out of the list of commands `--json` fills an envelope for.
    /// </summary>
    [Fact]
    public void The_gate_is_not_an_enveloped_command() =>
        Assert.DoesNotContain("agent gate", MachineOutput.Commands);

    private static (CommandStreams Streams, StringWriter Out, StringWriter Error) Captured()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        return (new CommandStreams(output, error), output, error);
    }
}
