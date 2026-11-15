using Heartbeat.Api.Auth;
using Heartbeat.Shared;

namespace Heartbeat.Api.Crew;

/// <summary>Domain profiles (space reference, mining, Antarctica, submarine, offshore) for Mission Control (HB-BE-11).</summary>
public static class McProfilesEndpoints
{
    private static readonly Lazy<IReadOnlyList<DomainProfileDto>> Profiles =
        new(() => DomainProfiles.All.Select(DomainProfiles.ToDto).ToList());

    public static IEndpointRouteBuilder MapMcProfilesEndpoints(this IEndpointRouteBuilder app)
    {
        // Static catalog data: any signed-in Mission Control user may read it.
        app.MapGet("/api/mc/v1/profiles", () => Results.Ok(Profiles.Value)).RequireAuthorization(AuthConstants.McPolicy);
        return app;
    }
}
