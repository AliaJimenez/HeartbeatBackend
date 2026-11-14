using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Heartbeat.Api.Auth;

public sealed class JwtTokenService
{
    private readonly string _issuer;
    private readonly byte[] _key;

    public JwtTokenService(IConfiguration configuration)
    {
        _issuer = configuration["Jwt:Issuer"] ?? "Heartbeat";
        var key = configuration["Jwt:Key"] ?? "dev-only-signing-key-change-me-please-32chars";
        _key = Encoding.UTF8.GetBytes(key);
    }

    public string CreateAccessToken(
        Guid userId,
        string displayName,
        string audience,
        Guid? missionId = null,
        IEnumerable<string>? permissions = null,
        TimeSpan? lifetime = null)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new(JwtRegisteredClaimNames.Name, displayName),
        };

        if (missionId.HasValue)
        {
            claims.Add(new Claim("mission", missionId.Value.ToString()));
        }

        foreach (var permission in permissions ?? Array.Empty<string>())
        {
            claims.Add(new Claim("perm", permission));
        }

        return CreateToken(claims, audience, lifetime ?? TimeSpan.FromMinutes(60));
    }

    public string CreateRefreshToken(Guid userId, string audience, TimeSpan? lifetime = null)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new("token_type", "refresh"),
        };

        return CreateToken(claims, $"{audience}-refresh", lifetime ?? TimeSpan.FromDays(14));
    }

    public ClaimsPrincipal? ValidateRefreshToken(string token, string audience)
    {
        // Keep claim names as issued ('sub', 'name'); the default maps 'sub' to ClaimTypes.NameIdentifier,
        // which made every refresh look like it had no user id.
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = _issuer,
            ValidateAudience = true,
            ValidAudience = $"{audience}-refresh",
            ValidateLifetime = true,
            IssuerSigningKey = new SymmetricSecurityKey(_key),
            ClockSkew = TimeSpan.FromMinutes(1),
        };

        try
        {
            var principal = handler.ValidateToken(token, parameters, out _);
            if (principal.FindFirst("token_type")?.Value != "refresh")
            {
                return null;
            }

            return principal;
        }
        catch
        {
            return null;
        }
    }

    private string CreateToken(IEnumerable<Claim> claims, string audience, TimeSpan lifetime)
    {
        var credentials = new SigningCredentials(new SymmetricSecurityKey(_key), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: _issuer,
            audience: audience,
            claims: claims,
            expires: DateTime.UtcNow.Add(lifetime),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}