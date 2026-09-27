using System.Net;
using System.Text;
using LocalAi.Installer.Core.Models;

namespace LocalAi.Installer.Core.Tests;

/// <summary>
/// Offline for models means the Ollama registry did not answer, so a refusal or a body without a
/// size has to read as an answer, and only a request that got no response as silence.
/// </summary>
public sealed class TheRegistrysAnswerIsToldFromItsSilenceTests
{
    [Fact]
    public async Task A_refusal_is_an_answer_without_a_size()
    {
        var answer = await Ask(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        Assert.Equal(new ModelSizeAnswer(true, null), answer);
    }

    [Fact]
    public async Task A_body_that_is_not_a_manifest_is_still_an_answer()
    {
        var answer = await Ask(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>captive portal</html>", Encoding.UTF8, "text/html"),
        });

        Assert.Equal(new ModelSizeAnswer(true, null), answer);
    }

    [Fact]
    public async Task A_manifest_is_an_answer_with_its_size()
    {
        var answer = await Ask(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"layers":[{"size":100},{"size":20}]}""",
                Encoding.UTF8,
                "application/json"),
        });

        Assert.Equal(new ModelSizeAnswer(true, 120), answer);
    }

    [Fact]
    public async Task A_request_that_gets_no_response_is_no_answer()
    {
        var answer = await Ask(_ => throw new HttpRequestException("No such host is known."));

        Assert.Equal(new ModelSizeAnswer(false, null), answer);
    }

    private static async Task<ModelSizeAnswer> Ask(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        using var client = new HttpClient(new Respond(respond))
        {
            BaseAddress = new Uri("https://registry.example.invalid/"),
        };
        using var registry = new OllamaRegistryClient(client);
        return await registry.GetDownloadSizeAsync("test:1", TestContext.Current.CancellationToken);
    }

    private sealed class Respond(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
