using Kinshout.Api.Middleware;

namespace Kinshout.Api.Tests;

public class ClientAuthMiddlewareTests
{
    [Theory]
    [InlineData("POST", "/api/videos/909adfca-b5e3-43e0-9a59-061068ed0b79/processing-result", true)]
    [InlineData("GET", "/api/videos/909adfca-b5e3-43e0-9a59-061068ed0b79/processing-result", false)]
    [InlineData("POST", "/api/videos/909adfca-b5e3-43e0-9a59-061068ed0b79/complete", false)]
    [InlineData("POST", "/api/videos/uploads", false)]
    [InlineData("POST", "/api/videos/not-a-guid/processing-result", false)]
    [InlineData("POST", "/api/videos/909adfca-b5e3-43e0-9a59-061068ed0b79/processing-result/extra", false)]
    public void OnlyTheVideoWorkerCallbackSkipsTheClientToken(string method, string path, bool expected)
    {
        Assert.Equal(expected, ClientAuthMiddleware.IsVideoWorkerCallback(method, path));
    }
}
