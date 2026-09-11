using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalAi.Contracts;
using LocalAi.Installer.Core.Planning;

namespace LocalAi.Installer.Core.Agents;

public sealed class ClaudeConfigurationAdapter(
    string homeDirectory,
    string installationDirectory,
    TimeProvider? timeProvider = null,
    Func<string, byte[]>? readBackOverride = null)
{
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Func<string, byte[]> readBack = readBackOverride ?? File.ReadAllBytes;

    public AgentConfigurationPlan Preview(AgentIntegrationChoice choice)
    {
        var files = new List<AgentConfigurationFilePlan>();
        if (choice is AgentIntegrationChoice.McpOnly or AgentIntegrationChoice.McpAndInstructions)
        {
            var path = Path.Combine(homeDirectory, ".claude.json");
            var before = ReadExisting(path);
            var after = UpdateJson(AgentConfigurationFileOperations.DecodeUtf8(before));
            if (!before.SequenceEqual(Encoding.UTF8.GetBytes(after)))
            {
                files.Add(AgentConfigurationFileOperations.FilePlan(path, before, after, timeProvider.GetUtcNow()));
            }
        }

        if (choice is AgentIntegrationChoice.InstructionsOnly or AgentIntegrationChoice.McpAndInstructions)
        {
            AddInstructions(files, Path.Combine(homeDirectory, ".claude", "CLAUDE.md"));
            // In the same plan as the core, deliberately: a plan is applied or rolled
            // back whole, and the state worth preventing is a CLAUDE.md pointing at a
            // skill whose write failed.
            AddSkill(files, Path.Combine(homeDirectory, ManagedInstructionBlock.SkillRelativePath));
        }

        return new("Claude", files, BuildPreview(files));
    }

    /// <summary>
    /// What disconnecting this client would change: the two managed server registrations go,
    /// and so does the managed instructions block.
    ///
    /// A file that does not exist is not created to be emptied — there is nothing of ours in it
    /// — and everything the user put in the files that do exist survives, because the same
    /// text-preserving rewrites the install uses are what take our entries back out. The plan
    /// is applied through <see cref="ApplyAsync"/> exactly like an installation's, so removal
    /// gets the same backups, the same read-back, and the same refusal on a concurrent edit.
    /// </summary>
    public AgentConfigurationPlan PreviewRemoval()
    {
        var files = new List<AgentConfigurationFilePlan>();
        var path = Path.Combine(homeDirectory, ".claude.json");
        var before = ReadExisting(path);
        if (before.Length > 0)
        {
            var after = RemoveManagedServers(AgentConfigurationFileOperations.DecodeUtf8(before));
            if (!before.SequenceEqual(Encoding.UTF8.GetBytes(after)))
            {
                files.Add(AgentConfigurationFileOperations.FilePlan(
                    path,
                    before,
                    after,
                    timeProvider.GetUtcNow()));
            }
        }

        RemoveInstructions(files, Path.Combine(homeDirectory, ".claude", "CLAUDE.md"));
        RemoveSkill(files, Path.Combine(homeDirectory, ManagedInstructionBlock.SkillRelativePath));
        return new("Claude", files, BuildPreview(files));
    }

    /// <summary>
    /// The text-search gate's registration, as a plan of its own rather than part of the client's.
    ///
    /// The reason is what a failed plan costs. `settings.json` is a file the person edits by hand,
    /// so a stray comma in it is an ordinary event — and a plan that cannot be applied is reported
    /// and skipped, leaving the rest of the installation alone. Put the gate in the client's plan
    /// and that stray comma takes the MCP registration with it: the most expensive thing in the
    /// installation lost to the least important (#423).
    ///
    /// Uninstalling is the same call with <paramref name="register"/> false, because "take it out"
    /// and "do not put it in" are the same instruction to this file.
    /// </summary>
    /// <param name="register">
    /// What the person chose. False with nothing of ours on disk plans nothing at all — an empty
    /// `hooks: {}` written into somebody's settings is an effect nobody consented to.
    /// </param>
    public AgentConfigurationPlan PreviewGate(bool register)
    {
        var files = new List<AgentConfigurationFilePlan>();
        var path = GateSettingsPath;
        var before = ReadExisting(path);
        var text = AgentConfigurationFileOperations.DecodeUtf8(before);
        string after;
        if (register)
        {
            // Allowed to throw: the person asked for this, and a settings file that cannot be
            // read is something they have to be told about rather than something to work around.
            after = ClaudeHookRegistration.Add(text, GateCommand);
        }
        else
        {
            // Allowed to throw, exactly as registering is. The swallow that used to be here
            // reasoned that a file this cannot read holds no entry of ours — which is false: a
            // trailing comma is enough to refuse the whole document, and the entry may well be
            // in it. An uninstall then planned nothing, said nothing, and left the hook behind
            // pointing at the launcher it had just deleted, while the removal screen promised
            // the hook was going. Throwing is what `PreviewRemoval` already does for the
            // neighbouring `.claude.json`, so the two files a removal touches fail alike rather
            // than one of them reporting a success it did not have.
            after = ClaudeHookRegistration.Remove(text);
        }

        if (!before.SequenceEqual(Encoding.UTF8.GetBytes(after)))
        {
            files.Add(AgentConfigurationFileOperations.FilePlan(
                path,
                before,
                after,
                timeProvider.GetUtcNow()));
        }

        return new(GateName, files, BuildPreview(files));
    }

    /// <summary>
    /// Named apart from the client's own plan, and not "Claude": the two are applied, journalled
    /// and reported as separate steps, so two plans under one name would be two lines nobody
    /// could tell apart — and would break every lookup that expects one plan per client.
    /// </summary>
    public const string GateName = "Claude text-search gate";

    /// <summary>
    /// Whether the gate is registered on this machine right now.
    ///
    /// This is what seeds the checkbox, and it is read rather than remembered: the installer keeps
    /// no record of a decision, so an update cannot silently restore something that was declined,
    /// and somebody who took the entry out by hand is shown the truth rather than a memory.
    /// </summary>
    public bool IsGateRegistered()
    {
        try
        {
            // By signature rather than by this installation's command: an entry a previous
            // install wrote at another directory is still ours, and is still what the run will
            // take out.
            return ClaudeHookRegistration.IsRegistered(
                AgentConfigurationFileOperations.DecodeUtf8(ReadExisting(GateSettingsPath)));
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public string GateSettingsPath => Path.Combine(homeDirectory, ".claude", "settings.json");

    /// <summary>
    /// Quoted whole, in the shape `CodeSearchTools.IndexCommand` already prints: a hook command is
    /// one command line rather than an executable and a list, and the installation path has
    /// spaces in it on most machines.
    /// </summary>
    private string GateCommand =>
        "\"" + Path.Combine(
            Path.GetFullPath(installationDirectory),
            "launcher",
            "localai-launcher.exe") + "\" run localai agent gate";

    public Task ApplyAsync(AgentConfigurationPlan plan, CancellationToken cancellationToken) =>
        AgentConfigurationFileOperations.ApplyAsync(plan, readBack, cancellationToken);

    private static string RemoveManagedServers(string before)
    {
        if (string.IsNullOrWhiteSpace(before))
        {
            return before;
        }

        JsonObject root;
        try
        {
            root = JsonNode.Parse(before) as JsonObject
                ?? throw new InvalidOperationException("Unsupported Claude MCP JSON layout.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Malformed Claude MCP JSON layout.", exception);
        }

        if (root["mcpServers"] is null)
        {
            return before;
        }

        if (root["mcpServers"] is not JsonObject servers)
        {
            throw new InvalidOperationException("Unsupported Claude MCP JSON layout.");
        }

        if (servers["codesearch"] is null && servers["locallm"] is null)
        {
            return before;
        }

        var rootStart = NextNonWhitespace(before, 0);
        if (rootStart < 0 || before[rootStart] != '{')
        {
            throw new InvalidOperationException("Unsupported Claude MCP JSON layout.");
        }

        var rootEnd = FindMatching(before, rootStart, '{', '}');
        var property = FindTopLevelProperty(before, rootStart + 1, rootEnd, "mcpServers");
        var colon = property < 0 ? -1 : before.IndexOf(':', property + "\"mcpServers\"".Length);
        var objectStart = colon < 0 ? -1 : NextNonWhitespace(before, colon + 1);
        if (objectStart < 0 || objectStart >= before.Length || before[objectStart] != '{')
        {
            throw new InvalidOperationException("Unsupported Claude MCP JSON layout.");
        }

        var objectEnd = FindMatching(before, objectStart, '{', '}');
        var bodyStart = objectStart + 1;
        var body = RemoveProperty(before[bodyStart..objectEnd], "codesearch");
        body = RemoveProperty(body, "locallm");
        return before[..bodyStart] + body + before[objectEnd..];
    }

    private string UpdateJson(string before)
    {
        JsonObject root;
        if (string.IsNullOrWhiteSpace(before))
        {
            root = [];
        }
        else
        {
            try
            {
                root = JsonNode.Parse(before) as JsonObject
                    ?? throw new InvalidOperationException("Unsupported Claude MCP JSON layout.");
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException("Malformed Claude MCP JSON layout.", exception);
            }
        }

        var serversNode = root["mcpServers"];
        if (serversNode is null)
        {
            return InsertMcpServers(before, ClientCommandPlan.Plan(installationDirectory));
        }

        if (serversNode is not JsonObject serverObject)
        {
            throw new InvalidOperationException("Unsupported Claude MCP JSON layout.");
        }

        foreach (var server in serverObject)
        {
            if (server.Key is "codesearch" or "locallm")
            {
                ValidateManagedServer(server.Value);
            }
        }

        var plan = ClientCommandPlan.Plan(installationDirectory);
        if (IsCurrent(serverObject, "codesearch", plan.CodeSearch) &&
            IsCurrent(serverObject, "locallm", plan.LocalLm))
        {
            return before;
        }

        return ReplaceManagedServers(before, plan, serverObject);
    }

    /// <summary>
    /// Whether the registration already says what this install would say.
    ///
    /// Compared by value, never by bytes. This file belongs to the client, which rewrites it
    /// constantly and reserialises the whole document in its own style, so a registration that
    /// is already correct still looks different from the text this adapter would produce. Byte
    /// comparison therefore reported a change on every single install: the file was rewritten,
    /// a backup was left behind, and nothing about the meaning had moved. Matching the client's
    /// formatting instead would only trade that for a guess about somebody else's serialiser.
    /// </summary>
    private static bool IsCurrent(
        JsonObject servers,
        string name,
        ClientToolRegistration registration)
    {
        if (servers[name] is not JsonObject server ||
            server["command"] is not JsonValue command ||
            !command.TryGetValue<string>(out var commandValue) ||
            !string.Equals(commandValue, registration.Command, StringComparison.Ordinal) ||
            server["args"] is not JsonArray args ||
            args.Count != registration.Arguments.Count)
        {
            return false;
        }

        for (var index = 0; index < args.Count; index++)
        {
            if (args[index] is not JsonValue argument ||
                !argument.TryGetValue<string>(out var value) ||
                !string.Equals(value, registration.Arguments[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static JsonObject ToServer(string command, IReadOnlyList<string> arguments) =>
        new()
        {
            ["command"] = command,
            ["args"] = new JsonArray(arguments.Select(argument => JsonValue.Create(argument)).ToArray<JsonNode?>()),
        };

    private static void ValidateManagedServer(JsonNode? node)
    {
        if (node is not JsonObject obj ||
            obj["command"] is not JsonValue command ||
            !command.TryGetValue<string>(out _) ||
            obj["args"] is not JsonArray args ||
            args.Any(argument => argument is not JsonValue value || !value.TryGetValue<string>(out _)))
        {
            throw new InvalidOperationException("Malformed Claude MCP JSON layout.");
        }
    }

    private static string ReplaceManagedServers(
        string json,
        ClientRegistrationPlan plan,
        JsonObject servers)
    {
        var rootStart = NextNonWhitespace(json, 0);
        if (rootStart < 0 || json[rootStart] != '{')
        {
            throw new InvalidOperationException("Unsupported Claude MCP JSON layout.");
        }

        var rootEnd = FindMatching(json, rootStart, '{', '}');
        var property = FindTopLevelProperty(json, rootStart + 1, rootEnd, "mcpServers");
        var colon = property < 0 ? -1 : json.IndexOf(':', property + "\"mcpServers\"".Length);
        var objectStart = colon < 0 ? -1 : NextNonWhitespace(json, colon + 1);
        if (objectStart < 0 || objectStart >= json.Length || json[objectStart] != '{')
        {
            throw new InvalidOperationException("Unsupported Claude MCP JSON layout.");
        }

        var objectEnd = FindMatching(json, objectStart, '{', '}');
        var bodyStart = objectStart + 1;
        var body = json[bodyStart..objectEnd];
        body = RemoveProperty(body, "codesearch");
        body = RemoveProperty(body, "locallm");
        var insert =
            FormatServer("codesearch", plan.CodeSearch, servers["codesearch"] as JsonObject) +
            "," + Environment.NewLine +
            FormatServer("locallm", plan.LocalLm, servers["locallm"] as JsonObject);
        body = string.IsNullOrWhiteSpace(body)
            ? Environment.NewLine + insert + Environment.NewLine
            : body.TrimEnd() + "," + Environment.NewLine + insert + Environment.NewLine;
        return json[..bodyStart] + body + json[objectEnd..];
    }

    private static string InsertMcpServers(string json, ClientRegistrationPlan plan)
    {
        var rootStart = NextNonWhitespace(json, 0);
        if (rootStart < 0)
        {
            var root = new JsonObject();
            root["mcpServers"] = new JsonObject
            {
                ["codesearch"] = ToServer(plan.CodeSearch.Command, plan.CodeSearch.Arguments),
                ["locallm"] = ToServer(plan.LocalLm.Command, plan.LocalLm.Arguments),
            };
            return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
        }

        if (json[rootStart] != '{')
        {
            throw new InvalidOperationException("Unsupported Claude MCP JSON layout.");
        }

        var rootEnd = FindMatching(json, rootStart, '{', '}');
        var beforeClose = json[..rootEnd].TrimEnd();
        var needsComma = beforeClose.Length > rootStart + 1;
        var property =
            (needsComma ? "," : string.Empty) + Environment.NewLine +
            "  \"mcpServers\": {" + Environment.NewLine +
            FormatServer("codesearch", plan.CodeSearch) + "," + Environment.NewLine +
            FormatServer("locallm", plan.LocalLm) + Environment.NewLine +
            "  }";
        return beforeClose + property + json[rootEnd..];
    }

    /// <summary>
    /// Rewrites the command and its arguments, and carries everything else in the entry across.
    ///
    /// The entry is replaced rather than edited in place, so anything this method does not
    /// re-emit is destroyed. A user who set an environment variable or a per-tool option on
    /// these servers would have lost it to a command-line update — the same way Codex lost its
    /// per-tool approvals before that adapter was taught to leave them alone.
    /// </summary>
    private static string FormatServer(
        string name,
        ClientToolRegistration registration,
        JsonObject? existing = null)
    {
        var preserved = existing is null
            ? string.Empty
            : string.Concat(existing
                .Where(property => property.Key is not ("command" or "args"))
                .Select(property =>
                    ",\"" + JsonEncodedText(property.Key) + "\":" +
                    (property.Value?.ToJsonString() ?? "null")));
        return "    \"" + name + "\": {\"command\":\"" + JsonEncodedText(registration.Command) +
            "\",\"args\":[" +
            string.Join(",", registration.Arguments.Select(argument => "\"" + JsonEncodedText(argument) + "\"")) +
            "]" + preserved + "}";
    }

    private static string JsonEncodedText(string value) =>
        JsonSerializer.Serialize(value)[1..^1];

    private static string RemoveProperty(string jsonObjectBody, string name)
    {
        var property = FindTopLevelPropertyInBody(jsonObjectBody, name);
        if (property < 0)
        {
            return jsonObjectBody;
        }

        var colon = jsonObjectBody.IndexOf(':', property + name.Length + 2);
        if (colon < 0)
        {
            throw new InvalidOperationException("Unsupported Claude MCP JSON layout.");
        }

        var valueStart = NextNonWhitespace(jsonObjectBody, colon + 1);
        if (valueStart < 0 || valueStart >= jsonObjectBody.Length || jsonObjectBody[valueStart] != '{')
        {
            throw new InvalidOperationException("Unsupported Claude MCP JSON layout.");
        }

        var valueEnd = FindMatching(jsonObjectBody, valueStart, '{', '}') + 1;
        var removeStart = property;
        while (removeStart > 0 && char.IsWhiteSpace(jsonObjectBody[removeStart - 1]))
        {
            removeStart--;
        }

        var removeEnd = valueEnd;
        while (removeEnd < jsonObjectBody.Length && char.IsWhiteSpace(jsonObjectBody[removeEnd]))
        {
            removeEnd++;
        }

        if (removeEnd < jsonObjectBody.Length && jsonObjectBody[removeEnd] == ',')
        {
            removeEnd++;
        }
        else if (removeStart > 0)
        {
            var comma = removeStart - 1;
            while (comma >= 0 && char.IsWhiteSpace(jsonObjectBody[comma]))
            {
                comma--;
            }

            if (comma >= 0 && jsonObjectBody[comma] == ',')
            {
                removeStart = comma;
            }
        }

        return jsonObjectBody[..removeStart] + jsonObjectBody[removeEnd..];
    }

    // The three scanners these call live in JsonTextSurgery now: settings.json needs the same
    // splicing, and a second brace matcher is a second set of quoting bugs (#423).
    private static int NextNonWhitespace(string value, int start) =>
        JsonTextSurgery.NextNonWhitespace(value, start);

    private static int FindTopLevelPropertyInBody(string body, string name) =>
        JsonTextSurgery.FindTopLevelProperty(body, 0, body.Length, name);

    private static int FindTopLevelProperty(string value, int start, int end, string name) =>
        JsonTextSurgery.FindTopLevelProperty(value, start, end, name);

    private static int FindMatching(string value, int start, char open, char close) =>
        JsonTextSurgery.FindMatching(value, start, open, close);

    private void AddInstructions(List<AgentConfigurationFilePlan> files, string path)
    {
        var before = ReadExisting(path);
        var updated = ManagedInstructionBlock.Upsert(AgentConfigurationFileOperations.DecodeUtf8(before));
        if (updated.Changed)
        {
            files.Add(AgentConfigurationFileOperations.FilePlan(path, before, updated.Content, timeProvider.GetUtcNow()));
        }
    }

    /// <summary>
    /// The reference material, written where Claude discovers a personal skill. The
    /// directory name is what names the command, so the frontmatter carries only a
    /// description — which is also the one part of it that stays in context whether or
    /// not the skill is ever invoked.
    /// </summary>
    private void AddSkill(List<AgentConfigurationFilePlan> files, string path)
    {
        var before = ReadExisting(path);
        var after = ManagedInstructionBlock.SkillFile();
        if (!before.SequenceEqual(Encoding.UTF8.GetBytes(after)))
        {
            files.Add(AgentConfigurationFileOperations.FilePlan(
                path,
                before,
                after,
                timeProvider.GetUtcNow()));
        }
    }

    /// <summary>
    /// The skill file goes only while it is still the file this installer wrote. Somebody who
    /// edited it keeps their edit: destroying it would be worse than leaving a file they can be
    /// told about, and being told is what the retained notice is for.
    ///
    /// The comparison is against the current version's text, so a skill written by an older
    /// LocalAi and never refreshed is retained rather than deleted. That is a real limit, and
    /// the alternative — a table of every text ever shipped — buys less than it costs.
    /// </summary>
    private void RemoveSkill(List<AgentConfigurationFilePlan> files, string path)
    {
        var before = ReadExisting(path);
        if (before.Length == 0 ||
            !before.SequenceEqual(Encoding.UTF8.GetBytes(ManagedInstructionBlock.SkillFile())))
        {
            return;
        }

        files.Add(AgentConfigurationFileOperations.DeletePlan(
            path,
            before,
            timeProvider.GetUtcNow()));
    }

    private void RemoveInstructions(List<AgentConfigurationFilePlan> files, string path)
    {
        var before = ReadExisting(path);
        if (before.Length == 0)
        {
            return;
        }

        var updated = ManagedInstructionBlock.Remove(
            AgentConfigurationFileOperations.DecodeUtf8(before));
        if (updated.Changed)
        {
            files.Add(AgentConfigurationFileOperations.FilePlan(path, before, updated.Content, timeProvider.GetUtcNow()));
        }
    }

    private static byte[] ReadExisting(string path) =>
        File.Exists(path) ? File.ReadAllBytes(path) : [];

    private static string BuildPreview(IEnumerable<AgentConfigurationFilePlan> files) =>
        string.Join(
            "\n---\n",
            files.Select(file =>
                file.Path + "\n" +
                "Before:\n" + AgentConfigurationFileOperations.Redact(file.BeforeText) + "\n" +
                "After:\n" + AgentConfigurationFileOperations.Redact(file.AfterText)));
}
