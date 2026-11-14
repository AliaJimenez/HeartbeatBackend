using System.Net;
using System.Net.Http.Headers;
using Heartbeat.Api.Auth;
using Heartbeat.Shared;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Heartbeat.Tests;

public class CrewReferenceRangesTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Path = "/api/crew/v1/reference-ranges";

    private readonly WebApplicationFactory<Program> _factory;

    public CrewReferenceRangesTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private HttpClient Client(string audience = AuthConstants.CrewAudience)
    {
        var jwt = _factory.Services.GetRequiredService<JwtTokenService>();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", jwt.CreateAccessToken(Guid.NewGuid(), "ana", audience));
        return client;
    }

    [Fact]
    public async Task Requires_a_crew_token()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync(Path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(AuthConstants.McAudience).GetAsync(Path)).StatusCode);
    }

    [Fact]
    public async Task Returns_raw_json_with_etag_equal_to_the_version()
    {
        var response = await Client().GetAsync(Path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal($"\"{ReferenceRanges.Version}\"", response.Headers.ETag?.Tag);
        Assert.Equal(ReferenceRanges.Json, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("\"{0}\"")]
    [InlineData("W/\"{0}\"")]
    [InlineData("\"other\", \"{0}\"")]
    [InlineData("*")]
    public async Task Returns_304_when_if_none_match_matches(string template)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Path);
        request.Headers.TryAddWithoutValidation("If-None-Match", string.Format(template, ReferenceRanges.Version));

        var response = await Client().SendAsync(request);

        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
        Assert.Equal($"\"{ReferenceRanges.Version}\"", response.Headers.ETag?.Tag);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Returns_200_when_if_none_match_is_stale()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Path);
        request.Headers.TryAddWithoutValidation("If-None-Match", "\"old-version\"");

        var response = await Client().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ReferenceRanges.Json, await response.Content.ReadAsStringAsync());
    }
}
