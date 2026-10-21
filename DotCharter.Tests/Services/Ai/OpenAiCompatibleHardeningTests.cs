using System.Net;
using DotCharter.Tests.Services.Diagnostics;
using DotCharter.Tests.Services.Update;
using DotCharter.Tone.Ai;
using DotCharter.Tone.Online;

namespace DotCharter.Tests.Services.Ai;

/// <summary>
///     The OpenAI-compatible provider sends the whole prompt, and often a bearer key, to the base URL
///     the user typed: that must be https unless it is this machine, a redirect must not carry the POST
///     body anywhere else, and what the server says back is redacted before it is logged.
/// </summary>
public sealed class OpenAiCompatibleHardeningTests
{
    private static AiProviderSettings Settings(string url)
        => new(AiProviderKind.OpenAiCompatible, "local-model", url);

    private static ToneDesignPrompt Prompt()
        => ToneDesignPromptBuilder.Build(new ToneDesignRequest("a clean tone", [], null));

    [Theory]
    [InlineData("http://api.example.com/v1")]
    [InlineData("http://192.168.1.20:1234")]
    [InlineData("http://localhost.evil.example")]
    [InlineData("ftp://localhost/v1")]
    [InlineData("not a url")]
    public void A_base_url_that_is_not_https_and_not_this_machine_is_refused(string url)
    {
        var thrown = Assert.Throws<ToneDesignException>(() => new OpenAiCompatibleToneModel(Settings(url)));

        Assert.Equal(ToneDesignFault.NotConfigured, thrown.Fault);
    }

    [Theory]
    [InlineData("http://localhost:1234")]
    [InlineData("http://127.0.0.1:11434")]
    [InlineData("http://127.5.5.5:8080/v1")]
    [InlineData("http://[::1]:1234")]
    [InlineData("http://studio.localhost:1234")]
    [InlineData("https://api.openai.com/v1")]
    [InlineData("https://192.168.1.20:1234")]
    public void Loopback_over_http_and_any_https_url_are_accepted(string url)
    {
        using var model = new OpenAiCompatibleToneModel(Settings(url), handler: new FakeHttpMessageHandler());

        Assert.Equal(AiProviderKind.OpenAiCompatible, model.Kind);
    }

    [Fact]
    public async Task A_redirect_is_an_error_and_is_never_followed_with_the_prompt()
    {
        var handler = new FakeHttpMessageHandler
        {
            Fallback = _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
                response.Headers.Location = new Uri("https://evil.example/collect");
                return response;
            }
        };
        using var model = new OpenAiCompatibleToneModel(Settings("https://llm.example.com/v1"), handler: handler);

        var thrown = await Assert.ThrowsAsync<ToneDesignException>(
            () => model.CompleteAsync(Prompt(), TestContext.Current.CancellationToken));

        Assert.Single(handler.Requests);
        Assert.Contains("redirect", thrown.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("evil.example", thrown.Detail ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public void The_shared_transport_for_model_calls_does_not_follow_redirects()
    {
        Assert.False(OnlineHttp.NoRedirectHandler.AllowAutoRedirect);
        Assert.True(OnlineHttp.NoRedirectHandler.PooledConnectionLifetime > TimeSpan.Zero);
    }

    [Fact]
    public async Task A_server_error_body_is_redacted_capped_and_free_of_control_characters_before_it_is_kept()
    {
        const string key = FakeSecrets.OpenAiKey;
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(
            $"{{\"error\":\"bad request for Bearer {key}\u0007\nAuthorization: Bearer abc.def-123 sk-other-9876543210 " +
            $"{new string('x', 5000)}\"}}",
            HttpStatusCode.InternalServerError);
        using var model = new OpenAiCompatibleToneModel(Settings("https://llm.example.com/v1"), key, handler: handler);

        var thrown = await Assert.ThrowsAsync<ToneDesignException>(
            () => model.CompleteAsync(Prompt(), TestContext.Current.CancellationToken));

        var detail = thrown.Detail!;
        Assert.DoesNotContain(key, detail, StringComparison.Ordinal);
        Assert.DoesNotContain("abc.def-123", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-other-9876543210", detail, StringComparison.Ordinal);
        Assert.DoesNotContain('\u0007', detail);
        Assert.DoesNotContain('\n', detail);
        Assert.True(detail.Length <= 300, $"{detail.Length} characters");
        Assert.StartsWith("HTTP 500", detail, StringComparison.Ordinal);
    }
}
