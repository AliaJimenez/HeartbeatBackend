using System.Security.Claims;
using Heartbeat.Api.Auth;
using Heartbeat.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Api.Live;

/// <summary>Resultado de decidir de qué astronauta puede ver datos quien llama. <see cref="Status"/> 200 = permitido.</summary>
public sealed record AccessResult(Guid? AstronautId, int Status = StatusCodes.Status200OK, string? Message = null)
{
    public bool Allowed => Status == StatusCodes.Status200OK;

    public IResult ToProblem() => Results.Problem(title: Message, statusCode: Status);
}

/// <summary>
/// Regla de acceso a los datos de un reloj: un tripulante ve solo los suyos (el astronauta sale del token, nunca de la
/// petición); MC con <c>vitals.view</c> (ya exigido por la política) puede pedir los de cualquiera con <c>astronautId</c>.
/// </summary>
public static class AstronautAccess
{
    public static bool IsCrew(ClaimsPrincipal user) => user.HasClaim("aud", AuthConstants.CrewAudience);

    public static async Task<AccessResult> ResolveAsync(
        ClaimsPrincipal user, Guid? requested, AppDbContext db, bool mcMustSpecify, CancellationToken ct)
    {
        if (IsCrew(user))
        {
            var sub = user.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
            if (!Guid.TryParse(sub, out var own))
            {
                return new AccessResult(null, StatusCodes.Status401Unauthorized, "Invalid token");
            }

            return requested is not null && requested != own
                ? new AccessResult(null, StatusCodes.Status403Forbidden, "A crew member can only see their own watch")
                : new AccessResult(own);
        }

        if (requested is null)
        {
            return mcMustSpecify
                ? new AccessResult(null, StatusCodes.Status400BadRequest, "astronautId is required")
                : new AccessResult(null);
        }

        return await db.Astronauts.AnyAsync(a => a.Id == requested, ct)
            ? new AccessResult(requested)
            : new AccessResult(null, StatusCodes.Status404NotFound, "Astronaut not found");
    }
}
