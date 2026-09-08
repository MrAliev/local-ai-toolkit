using LocalAi.Cli;

namespace LocalAi.IntegrationTests;

/// <summary>
/// The other half of the rule the block states, and the half nothing enforced.
///
/// Machine output goes to a local model: `triage_log` for something short, `localai triage` on the
/// command line for a whole log, which prints where it has got to while it runs. What happened
/// instead was a 32 MB Ollama server log read with `awk` and `tail`, several times over, with the
/// findings summarised into the reply — every line of it paid for in cloud tokens, and the local
/// model that reads it for nothing left idle. The text-search gate did not cover it: it names
/// `grep` and its kin, and `awk` is not one of them.
///
/// What is under test is the same judgement as the search gate's: given the payload the hook is
/// handed, is this a whole log going into a reply, or a bounded look at one. A bounded look is not
/// refused — watching the end of a running build is what the block asks for.
/// </summary>
public sealed class AWholeLogIsRefusedBeforeItRunsTests
{
    [Theory]
    // The plain readers, which have no bounded form.
    [InlineData("cat C:\\Users\\someone\\AppData\\Local\\Ollama\\server.log")]
    [InlineData("type build.log")]
    [InlineData("more install.log")]
    [InlineData("less /var/log/syslog.log")]
    // What actually happened, twice: a whole log through awk, its summary into the reply.
    [InlineData("awk '/error/ {print}' server.log")]
    [InlineData("awk '{n++} END {print n}' diagnostics.jsonl")]
    // A count that is not a bounded look. Twenty lines answers "is it moving"; five thousand is
    // the file.
    [InlineData("tail -n 5000 build.log")]
    [InlineData("head -n 1000 build.log")]
    [InlineData("tail -5000 build.log")]
    // Following never ends, so it is never bounded however small the count beside it.
    [InlineData("tail -f dev.log")]
    [InlineData("tail --follow=name -n 20 dev.log")]
    [InlineData("Get-Content app.log -Wait")]
    // The runtime's own machine output, which is where this was learned.
    [InlineData("cat diagnostics.jsonl")]
    [InlineData("Get-Content C:\\Users\\someone\\AppData\\Local\\LocalAi\\diagnostics.jsonl")]
    // A directory that says what is in it, for files that do not carry the extension.
    [InlineData("cat LocalAi-installer-logs/install-20260908.txt")]
    [InlineData("cat logs/run.txt")]
    // Behind the things that start another command, exactly as the search gate handles them.
    [InlineData("cd R:/LocalAi && cat server.log")]
    [InlineData("sh -c 'cat server.log'")]
    [InlineData("ls | cat build.log")]
    public void A_whole_log_is_refused(string command) =>
        Assert.True(WholeLogRefusal.Refuses(command), command);

    [Theory]
    // The bounded look the block asks for while a long run is going.
    [InlineData("tail -20 build.log")]
    [InlineData("tail -n 50 server.log")]
    [InlineData("head -n 50 install.log")]
    [InlineData("Get-Content build.log -TotalCount 40")]
    [InlineData("Get-Content build.log -Tail 20")]
    // A line range is a bounded look spelled another way.
    [InlineData("sed -n '100,140p' server.log")]
    // Not a log. Source, configuration and data are read the ordinary way.
    [InlineData("cat src/LocalAi.Broker/Program.cs")]
    [InlineData("cat package.json")]
    [InlineData("Get-Content .claude/settings.json")]
    [InlineData("cat R:/LocalAi/model-routing.json")]
    // The tool this refusal points at must not be refused by it.
    [InlineData("localai triage C:\\Users\\someone\\AppData\\Local\\Ollama\\server.log")]
    [InlineData("localai-launcher.exe run localai triage build.log")]
    // The word in prose, and a path in a message, are not a read.
    [InlineData("git commit -m 'stop reading server.log into the reply'")]
    [InlineData("echo \"see build.log\"")]
    // Writing one is not reading one.
    [InlineData("dotnet test LocalAi.slnx > build.log 2>&1")]
    // Asking how big it is, or when it changed, reads none of it.
    [InlineData("ls -la server.log")]
    [InlineData("stat -c%s diagnostics.jsonl")]
    public void A_bounded_look_or_a_file_that_is_not_a_log_is_allowed(string command) =>
        Assert.False(WholeLogRefusal.Refuses(command), command);

    /// <summary>
    /// The gate answers for one tool at a time, and a line can be both. `cat build.log | grep
    /// error` sweeps a tree and pours a log, and the log is the expensive half — sending the
    /// reader to `search_code` would name the wrong tool and leave the cost unmentioned.
    /// </summary>
    [Fact]
    public void A_line_that_is_both_is_answered_as_the_log_it_reads()
    {
        var reason = TextSearchRefusal.ReasonFor("Bash", "cat build.log | grep error");

        Assert.NotNull(reason);
        Assert.Contains("localai triage", reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The refusal names both surfaces, because picking the wrong one is the half that survived
    /// remembering the rule: an MCP call says nothing between the call and the answer, which on a
    /// large log is minutes of silence.
    /// </summary>
    [Fact]
    public void The_refusal_names_both_surfaces_and_why_the_command_line_is_one_of_them()
    {
        var reason = TextSearchRefusal.ReasonFor("Bash", "cat server.log");

        Assert.NotNull(reason);
        Assert.Contains("triage_log", reason, StringComparison.Ordinal);
        Assert.Contains("localai triage", reason, StringComparison.Ordinal);
        Assert.Contains("cloud tokens", reason, StringComparison.Ordinal);
    }
}
