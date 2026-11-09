using System.Net;
using System.Text;
using System.Text.Json;
using Kinshout.Api.Dtos;
using Kinshout.ExternalImporter.Configuration;
using Kinshout.ExternalImporter.Import;
using Kinshout.ExternalImporter.Providers;

namespace Kinshout.Api.Tests;

public sealed class DiscussionSourceIngestionTests
{
    [Fact]
    public void ImporterPayload_RoundTripsActualCountsAndReactionsIntoApiContract()
    {
        var input = new SourceFeedDiscussion
        {
            ExternalId = "sample", ExternalUrl = "https://www.youtube.com/watch?v=sample12345",
            Title = "Musique", Body = "Un nouveau clip", ViewCount = 12345, LikeCount = 0,
            SourceReactions = ["Le rythme est entraînant"],
        };
        var mapped = DiscussionMapper.ToImportDto(input, new ExternalProviderSettings { Provider = "youtube" }, DateTime.UtcNow);
        var json = JsonSerializer.Serialize(mapped, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var api = JsonSerializer.Deserialize<Kinshout.Api.Dtos.ImportExternalDiscussionDto>(json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(12345, api.SourceViewCount);
        Assert.Equal(0, api.SourceLikeCount);
        Assert.Equal(input.SourceReactions, api.SourceReactions);
    }

    [Fact]
    public async Task OfficialYouTubeComments_AreOptionalAndExcludeAuthorIdentity()
    {
        var variable = "KINSHOUT_IMPORTER_FETCH_SOURCE_REACTIONS";
        var previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            var handler = new Handler();
            using var http = new HttpClient(handler);
            Environment.SetEnvironmentVariable(variable, "false");
            Assert.Null(await YouTubeSourceReactions.FetchWhenEnabledAsync(http, "test-key", "video12345", default));
            Assert.Equal(0, handler.Calls);
            Environment.SetEnvironmentVariable(variable, "true");
            Assert.Equal(new[] { "Belle chorégraphie" }, await YouTubeSourceReactions.FetchWhenEnabledAsync(http, "test-key", "video12345", default));
            Assert.Equal(1, handler.Calls);
            Assert.Contains("/youtube/v3/commentThreads", handler.Url);
            handler.Status = HttpStatusCode.Forbidden;
            Assert.Null(await YouTubeSourceReactions.FetchWhenEnabledAsync(http, "test-key", "video12345", default));
        }
        finally { Environment.SetEnvironmentVariable(variable, previous); }
    }

    [Fact]
    public void SocialPayload_UsesOnlySuppliedCommentText_AndDoesNotInventMissingReactions()
    {
        using var input = JsonDocument.Parse("""
            {"comments":[{"text":"Très beau spectacle","author":"Not retained"},"Quelle énergie",{"id":"ignored"}]}
            """);
        Assert.Equal(new[] { "Très beau spectacle", "Quelle énergie" }, SourceReactionReader.Read(input.RootElement));
        using var countsOnly = JsonDocument.Parse("""{"commentsCount":500,"replies":20}""");
        Assert.Null(SourceReactionReader.Read(countsOnly.RootElement));
    }

    private sealed class Handler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Url { get; private set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Url = request.RequestUri?.ToString();
            return Task.FromResult(new HttpResponseMessage(Status)
            {
                Content = new StringContent("""
                    {"items":[{"snippet":{"topLevelComment":{"snippet":{"textDisplay":"Belle chorégraphie","authorDisplayName":"Not retained"}}}}]}
                    """, Encoding.UTF8, "application/json"),
            });
        }
    }
}
