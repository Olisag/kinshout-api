using System.Net;
using System.Text;
using System.Text.Json;
using Kinshout.Api.Configuration;
using Kinshout.Api.Services;
using Microsoft.Extensions.Options;

namespace Kinshout.Api.Tests;

public class DiscussionSeedCommentGeneratorTests
{
    [Theory]
    [InlineData("Campagne électorale", "Un candidat présente son programme.")]
    [InlineData("Politique", "Une discussion publique.")]
    [InlineData("Election results", "The results are announced.")]
    public async Task PoliticalSource_AbstainsWithoutModelCall(string title, string body)
    {
        using var handler = new ResponseHandler("Une réaction neutre.");
        var generator = Create(handler);
        Assert.Null(await generator.GenerateAsync(new(title, body, [], "neutre"), default));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("Les élections sont importantes pour notre avenir.")]
    [InlineData("Votez pour notre candidat !")]
    public async Task PoliticalOutput_IsRejectedEvenForCulturalSource(string output)
    {
        using var handler = new ResponseHandler(output);
        Assert.Null(await Create(handler).GenerateAsync(CultureInput(), default));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task PoliticalSourceReaction_AbstainsWithoutModelCall()
    {
        using var handler = new ResponseHandler("Une réaction neutre.");
        var input = CultureInput() with { Reactions = ["Votez pour notre candidat !"] };
        Assert.Null(await Create(handler).GenerateAsync(input, default));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task NonpoliticalUseOfParti_IsNotBlocked()
    {
        const string text = "Le rythme est parti très fort dès les premières notes.";
        using var handler = new ResponseHandler(text);
        Assert.Equal(text, await Create(handler).GenerateAsync(CultureInput() with { Body = text }, default));
    }

    [Fact]
    public async Task OversizedOutput_IsRejected()
    {
        using var handler = new ResponseHandler(new string('a', 221));
        Assert.Null(await Create(handler).GenerateAsync(CultureInput(), default));
    }

    [Fact]
    public async Task ConcreteCulturalOutput_IsKept()
    {
        const string text = "Cette guitare acoustique donne une belle couleur au morceau.";
        using var handler = new ResponseHandler(text);
        Assert.Equal(text, await Create(handler).GenerateAsync(CultureInput(), default));
    }

    private static DiscussionSeedInput CultureInput() => new("Rumba acoustique", "Une répétition de rumba.", [], "curieux");
    private static DiscussionSeedCommentGenerator Create(ResponseHandler handler) => new(
        new Clients(handler), Options.Create(new OpenAiSettings { ApiKey = "test-only", ModerationEnabled = true }));
    private sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
    private sealed class ResponseHandler(string output) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var content = JsonSerializer.Serialize(new { comment = output });
            var response = JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") });
        }
    }
}
