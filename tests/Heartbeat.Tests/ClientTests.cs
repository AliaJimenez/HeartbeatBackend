using Heartbeat.Client;
using Heartbeat.Shared;

namespace Heartbeat.Tests;

public class ClientTests
{
    [Fact]
    public async Task FakeClient_LoginAndSync_Works()
    {
        using var client = new FakeCrewApiClient();

        var login = await client.LoginAsync(new LoginRequest("a.rivera", "password", "device-1"));

        Assert.NotNull(login.AccessToken);
        Assert.Equal("a.rivera", login.Profile.UserName);

        var sync = await client.SyncAsync(new SyncRequest(
            "device-1",
            null,
            "2026.11.14-1",
            null,
            Array.Empty<CheckUpDto>(),
            Array.Empty<MessageDto>()));

        Assert.Empty(sync.Rejected);
    }
}