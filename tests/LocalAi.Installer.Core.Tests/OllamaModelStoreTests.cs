using LocalAi.Contracts;
using LocalAi.Installer.Core.Models;

namespace LocalAi.Installer.Core.Tests;

/// <summary>
/// Which models Ollama already holds, read from its store on disk: an installation from before the
/// model setup was recorded is judged by that, and asking Ollama or the broker would start them.
/// </summary>
public sealed class OllamaModelStoreTests : IDisposable
{
    private static readonly string[] Catalogue = ["qwen3-embedding:8b-q8_0", "gpt-oss:20b"];

    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "localai-ollama-store-" + Guid.NewGuid().ToString("N"));

    public OllamaModelStoreTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_store_holding_one_of_the_models_holds_it(bool isDefault)
    {
        var store = Store(isDefault);
        Manifest(store, "registry.ollama.ai", "library", "gpt-oss", "20b");

        Assert.True(store.HoldsAny(Catalogue));
    }

    [Fact]
    public void A_store_holding_only_other_models_holds_none_of_these()
    {
        var store = Store(isDefault: true);
        Manifest(store, "registry.ollama.ai", "library", "llama3", "latest");
        Manifest(store, "registry.ollama.ai", "library", "gpt-oss", "120b");

        Assert.False(store.HoldsAny(Catalogue));
    }

    [Fact]
    public void A_model_outside_the_library_is_found_under_its_own_namespace()
    {
        var store = Store(isDefault: true);
        Manifest(store, "registry.ollama.ai", "someone", "embedder", "latest");

        Assert.True(store.HoldsAny(["someone/embedder"]));
        Assert.False(store.HoldsAny(["embedder"]));
    }

    /// <summary>Where Ollama's default store is missing, nothing is taken to have been pulled.</summary>
    [Fact]
    public void A_default_store_that_does_not_exist_holds_none()
    {
        var store = new OllamaModelStore(Path.Combine(root, "absent"), IsDefault: true);

        Assert.False(store.HoldsAny(Catalogue));
    }

    /// <summary>A folder somebody chose that is not there says nothing about where the models are.</summary>
    [Fact]
    public void A_named_store_that_is_not_there_cannot_be_read()
    {
        var store = new OllamaModelStore(Path.Combine(root, "unplugged"), IsDefault: false);

        Assert.Null(store.HoldsAny(Catalogue));
    }

    /// <summary>The broker starts Ollama with the folder its launch record names.</summary>
    [Fact]
    public void The_folder_the_launch_record_names_comes_first()
    {
        var named = Path.Combine(root, "models-named");
        Directory.CreateDirectory(named);
        new OllamaLaunchRecordStore(root).Save(@"C:\absent\ollama.exe", "0.12.0", named);

        var store = OllamaModelStore.Locate(root, Path.Combine(root, "models-env"), Path.Combine(root, "profile"));

        Assert.Equal(new OllamaModelStore(named, IsDefault: false), store);
    }

    [Fact]
    public void Otherwise_the_folder_Ollama_is_told_through_its_environment()
    {
        var environment = Path.Combine(root, "models-env");

        var store = OllamaModelStore.Locate(root, environment, Path.Combine(root, "profile"));

        Assert.Equal(new OllamaModelStore(environment, IsDefault: false), store);
    }

    [Fact]
    public void Otherwise_Ollamas_own_default_under_the_profile()
    {
        var profile = Path.Combine(root, "profile");

        var store = OllamaModelStore.Locate(root, environmentModels: null, profile);

        Assert.Equal(new OllamaModelStore(Path.Combine(profile, ".ollama", "models"), IsDefault: true), store);
    }

    /// <summary>A relative folder is resolved against a directory only Ollama knows.</summary>
    [Fact]
    public void A_relative_folder_in_the_environment_cannot_be_located()
    {
        Assert.Null(OllamaModelStore.Locate(root, "models", Path.Combine(root, "profile")));
    }

    private OllamaModelStore Store(bool isDefault)
    {
        var directory = Path.Combine(root, "models");
        Directory.CreateDirectory(Path.Combine(directory, "manifests"));
        return new OllamaModelStore(directory, isDefault);
    }

    private static void Manifest(OllamaModelStore store, params string[] path)
    {
        var file = Path.Combine([store.Directory, "manifests", .. path]);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "{}");
    }
}
