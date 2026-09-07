using System.Text;
using LocalAi.Installer.Core.Agents;
using LocalAi.Installer.Core.Planning;

namespace LocalAi.Installer.Core.Tests;

/// <summary>
/// The gate's registration is its own plan, never part of the one that registers the MCP servers
/// and writes the instructions.
///
/// The reason is what a plan does when it fails: `ApplyAgentConfigurationAsync` catches a plan that
/// cannot be applied, reports it and leaves the rest of the installation alone. Put the gate in the
/// client's plan and an unreadable `settings.json` — a file the person edits by hand, so a
/// misplaced comma is an ordinary event — takes the MCP registration down with it. That is the
/// most expensive thing in the installation being lost to the least important (#423).
/// </summary>
public sealed class TheGateIsPlannedOnItsOwnTests : IDisposable
{
    private readonly string home = Path.Combine(
        Path.GetTempPath(),
        "localai-gate-plan-" + Guid.NewGuid().ToString("N"));

    private readonly string install = @"C:\LocalAi\bin";
    private readonly DateTimeOffset now = new(2026, 9, 7, 10, 11, 12, TimeSpan.Zero);

    /// <summary>
    /// Without the mark, which is the shape the client writes and the shape this repository
    /// requires. `Encoding.UTF8` emits one, and a file carrying it decodes to the same text while
    /// its bytes differ — so "nothing changed" arrives here as a planned write of a document
    /// identical to the one on disk, and the mistake reads as a fault in the code under test.
    /// </summary>
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    [Fact]
    public void Registering_the_gate_plans_settings_json_and_nothing_else()
    {
        var plan = Adapter().PreviewGate(register: true);

        var file = Assert.Single(plan.Files);
        Assert.EndsWith(
            Path.Combine(".claude", "settings.json"),
            file.Path,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// And the client's own plan does not touch it, whichever integration is chosen — otherwise
    /// the separation is a claim rather than a fact.
    /// </summary>
    [Theory]
    [InlineData(AgentIntegrationChoice.McpOnly)]
    [InlineData(AgentIntegrationChoice.InstructionsOnly)]
    [InlineData(AgentIntegrationChoice.McpAndInstructions)]
    public void The_client_plan_never_touches_settings_json(AgentIntegrationChoice choice)
    {
        var plan = Adapter().Preview(choice);

        Assert.DoesNotContain(
            plan.Files,
            file => file.Path.EndsWith("settings.json", StringComparison.Ordinal));
    }

    /// <summary>
    /// The rule that makes "declined" survive an update without anything being remembered: when
    /// the box is unticked and there is nothing of ours on disk, the run does not open the file
    /// for writing at all. An empty `hooks: {}` written into somebody's settings is an effect
    /// nobody consented to, and it would reformat their file on the way.
    /// </summary>
    [Fact]
    public void Declining_when_nothing_is_registered_writes_nothing()
    {
        Directory.CreateDirectory(Path.Combine(home, ".claude"));
        File.WriteAllText(
            Path.Combine(home, ".claude", "settings.json"),
            "{\n  \"theme\": \"dark\"\n}",
            Utf8);

        Assert.Empty(Adapter().PreviewGate(register: false).Files);
    }

    [Fact]
    public void Declining_when_it_is_registered_takes_the_entry_out()
    {
        Register();

        var file = Assert.Single(Adapter().PreviewGate(register: false).Files);

        Assert.Equal("{\n  \"theme\": \"dark\"\n}", file.AfterText, StringComparer.Ordinal);
    }

    [Fact]
    public void Registering_what_is_already_registered_writes_nothing()
    {
        Register();

        Assert.Empty(Adapter().PreviewGate(register: true).Files);
    }

    /// <summary>
    /// Seeding the checkbox: the installer asks the machine rather than remembering a decision.
    /// A person who removed the entry by hand gets an unticked box, which is the truth, where a
    /// remembered "you agreed once" would be a lie.
    /// </summary>
    [Fact]
    public void Whether_it_is_registered_is_read_from_the_file()
    {
        Assert.False(Adapter().IsGateRegistered());

        Register();

        Assert.True(Adapter().IsGateRegistered());
    }

    /// <summary>
    /// An installation that moved is still an installation. The launcher path is part of the
    /// command, so asking "is this exact command registered" answers no for an entry a previous
    /// install wrote at another directory — and the entry is ours, `Remove` recognises it by its
    /// signature and takes it out. That gap is a lie in the review line rather than a cosmetic
    /// one: the box would show unticked, the run would report "nothing is written", and it would
    /// then remove an entry the person never agreed to lose.
    /// </summary>
    [Fact]
    public void An_entry_left_by_an_install_at_another_path_still_ticks_the_box()
    {
        var path = Path.Combine(home, ".claude", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            ClaudeHookRegistration.Add(
                "{}",
                "\"D:\\Elsewhere\\launcher\\localai-launcher.exe\" run localai agent gate"),
            Utf8);

        Assert.True(Adapter().IsGateRegistered());
        Assert.Single(Adapter().PreviewGate(register: false).Files);
    }

    /// <summary>
    /// Taking the entry out of a file nobody can parse fails loudly rather than reporting nothing
    /// to do.
    ///
    /// The swallow this replaces was silent at the worst possible moment. `Parse` refuses any
    /// document the JSON reader rejects — a trailing comma is enough — so an uninstall over a
    /// hand-edited settings file planned zero changes, printed no notice, and left the hook
    /// behind pointing at a launcher it had just deleted. The removal screen meanwhile promised
    /// "the hook that refuses text searches" was going.
    ///
    /// Throwing puts it through the machinery that already exists for a config the adapter
    /// refuses to rewrite: the plan fails, it is reported by name, and the rest of the removal
    /// carries on.
    /// </summary>
    [Fact]
    public void Taking_the_entry_out_of_a_file_that_cannot_be_read_fails_rather_than_reporting_nothing()
    {
        Directory.CreateDirectory(Path.Combine(home, ".claude"));
        File.WriteAllText(
            Path.Combine(home, ".claude", "settings.json"),
            "{\n  \"theme\": \"dark\",\n}",
            Utf8);

        Assert.Throws<InvalidOperationException>(() => Adapter().PreviewGate(register: false));
    }

    /// <summary>
    /// A settings file nobody can parse is not a reason to refuse the installation, and not a
    /// reason to overwrite it either: the question "is the gate registered" is answered no, and
    /// the plan that would write to it is the only thing that fails.
    /// </summary>
    [Fact]
    public void A_settings_file_that_cannot_be_read_answers_no_rather_than_throwing()
    {
        Directory.CreateDirectory(Path.Combine(home, ".claude"));
        File.WriteAllText(Path.Combine(home, ".claude", "settings.json"), "{ not json", Utf8);

        Assert.False(Adapter().IsGateRegistered());
    }

    private void Register()
    {
        var path = Path.Combine(home, ".claude", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\n  \"theme\": \"dark\"\n}", Utf8);
        File.WriteAllText(
            path,
            Assert.Single(Adapter().PreviewGate(register: true).Files).AfterText,
            Utf8);
    }

    private ClaudeConfigurationAdapter Adapter() =>
        new(home, install, new FixedTimeProvider(now));

    public void Dispose()
    {
        if (Directory.Exists(home))
        {
            Directory.Delete(home, recursive: true);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
