using System.Diagnostics;
using System.Text.Json;
using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

public sealed class OllamaModelDirectoryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "localai-model-directory-" + Guid.NewGuid().ToString("N"));
    private string Models => Path.Combine(root, "models");
    private string Executable => Path.Combine(root, "ollama.exe");
    private OllamaLaunchRecordStore Store => new(root);
    public OllamaModelDirectoryTests() { Directory.CreateDirectory(Models); File.WriteAllText(Executable, "fixture"); }

    [Fact]
    public void Configured_root_round_trips_and_survives_a_later_installer_version_update()
    {
        Store.Save(Executable, "fixture-1", Models);
        var configured = Assert.IsType<OllamaLaunchRecord>(Store.Read());
        Assert.Equal(2, configured.SchemaVersion); Assert.Equal(Models, configured.ModelsDirectory);
        Store.Save(Executable, "fixture-2");
        Assert.Equal(Models, Store.Read()!.ModelsDirectory); Assert.Equal("fixture-2", Store.Read()!.Version);
    }

    [Fact]
    public void Legacy_save_keeps_the_legacy_schema_without_an_extra_null_property()
    {
        Store.Save(Executable, "fixture");
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, OllamaLaunchRecord.FileName)));
        Assert.Equal(1, document.RootElement.GetProperty("SchemaVersion").GetInt32());
        Assert.False(document.RootElement.TryGetProperty("ModelsDirectory", out _)); Assert.Null(Store.Read()!.ModelsDirectory);
    }

    [Theory]
    [InlineData(1, "relative")]
    [InlineData(2, "relative")]
    [InlineData(2, "")]
    [InlineData(2, "\\\\server\\models")]
    public void Invalid_configured_roots_do_not_fall_back_to_a_legacy_record(int schema, string models)
    {
        WriteRecord(new(schema, Executable, "fixture", models));
        Assert.Null(Store.Read());
    }

    [Fact]
    public void Only_the_child_process_receives_the_explicit_model_directory()
    {
        WriteRecord(new(2, Executable, "fixture", Models));
        var before = Environment.GetEnvironmentVariable("OLLAMA_MODELS");
        ProcessStartInfo? started = null;
        var starter = Starter(info => started = info);
        starter.OnDiagnostic(Diagnostic());
        Assert.NotNull(started); Assert.Equal(Executable, started.FileName);
        Assert.Equal(["serve"], started.ArgumentList); Assert.Equal(Models, started.Environment["OLLAMA_MODELS"]);
        Assert.False(started.UseShellExecute); Assert.True(started.CreateNoWindow);
        Assert.Equal(before, Environment.GetEnvironmentVariable("OLLAMA_MODELS"));
    }

    [Fact]
    public void A_missing_configured_directory_prevents_start_instead_of_using_the_default_store()
    {
        WriteRecord(new(2, Executable, "fixture", Path.Combine(root, "missing")));
        var started = new List<ProcessStartInfo>(); var reports = new List<string>();
        var starter = Starter(started.Add, reports);
        starter.OnDiagnostic(Diagnostic());
        Assert.Empty(started); Assert.True(starter.HasAttempted);
        Assert.Contains(reports, line => line.Contains("could not be started", StringComparison.Ordinal));
    }

    [Fact]
    public void An_existing_server_is_never_replaced_to_apply_the_new_directory()
    {
        WriteRecord(new(2, Executable, "fixture", Models));
        var started = new List<ProcessStartInfo>();
        var starter = new BackendStarter(new Uri("http://127.0.0.1:11434"), Store, _ => { }, isListening: _ => true, startProcess: started.Add);
        starter.OnDiagnostic(Diagnostic()); Assert.Empty(started); Assert.False(starter.HasAttempted);
    }

    private BackendStarter Starter(Action<ProcessStartInfo> start, List<string>? reports = null) =>
        new(new Uri("http://127.0.0.1:11434"), Store, line => reports?.Add(line), isListening: _ => false, startProcess: start);
    private static BrokerHostDiagnostic Diagnostic() => new(Guid.Empty, "worker", Guid.Empty, "schedule", nameof(HttpRequestException));
    private void WriteRecord(OllamaLaunchRecord record) => File.WriteAllText(Path.Combine(root, OllamaLaunchRecord.FileName), JsonSerializer.Serialize(record));
    public void Dispose() => Directory.Delete(root, recursive: true);
}
