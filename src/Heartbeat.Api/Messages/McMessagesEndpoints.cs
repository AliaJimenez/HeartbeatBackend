using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Heartbeat.Api.Auth;
using Heartbeat.Api.Data;
using Heartbeat.Api.Data.Entities;
using Heartbeat.Shared;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Api.Messages;

/// <summary>Mission Control side of the astronaut conversation (CONTRACTS.md §6, MSG-BACKEND / MR-11).</summary>
/// <remarks>
/// Astronaut messages and reports arrive through <c>POST /api/crew/v1/sync</c>. Replies written here are stored with
/// <c>ReceivedAt = null</c> and delivered in <c>inbound.messages</c> of the astronaut's next sync, which then stamps
/// <c>ReceivedAt</c>.
/// </remarks>
public static class McMessagesEndpoints
{
    public const int MaxSubjectLength = 120;
    public const int MaxBodyLength = 4000;
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;

    public static IEndpointRouteBuilder MapMcMessagesEndpoints(this IEndpointRouteBuilder app)
    {
        var mc = app.MapGroup("/api/mc/v1");

        mc.MapGet("/crew/{id:guid}/messages", ListAsync).RequirePermission(Permissions.MessagesView);
        mc.MapPost("/crew/{id:guid}/messages", SendAsync).RequirePermission(Permissions.MessagesSend);
        mc.MapPost("/messages/{id:guid}/read", MarkReadAsync).RequirePermission(Permissions.MessagesView);

        return app;
    }

    private static async Task<IResult> ListAsync(
        Guid id, AppDbContext db, CancellationToken ct, int page = 1, int pageSize = DefaultPageSize)
    {
        var errors = new Dictionary<string, string[]>();
        if (page < 1)
        {
            errors["page"] = ["page must be 1 or greater."];
        }

        if (pageSize is < 1 or > MaxPageSize)
        {
            errors["pageSize"] = [$"pageSize must be between 1 and {MaxPageSize}."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var astronautName = await db.Astronauts.AsNoTracking().Where(a => a.Id == id).Select(a => a.DisplayName).FirstOrDefaultAsync(ct);
        if (astronautName is null)
        {
            return AstronautNotFound();
        }

        // SQLite cannot ORDER BY DateTimeOffset, so the (small) conversation is ordered and paged in memory.
        var all = await db.Messages.AsNoTracking().Where(m => m.AstronautId == id).ToListAsync(ct);
        var pageRows = all.OrderByDescending(m => m.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize).ToList();

        var senderIds = pageRows.Where(m => m.SenderUserId != null).Select(m => m.SenderUserId!.Value).Distinct().ToList();
        var senders = senderIds.Count == 0
            ? []
            : await db.PortalUsers.AsNoTracking().Where(u => senderIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);

        var items = pageRows.Select(m => ToDto(m, SenderName(m, astronautName, senders))).ToList();
        return Results.Ok(new Paged<MessageDto>(items, page, pageSize, all.Count));
    }

    private static async Task<IResult> SendAsync(
        Guid id, SendMessageRequest request, ClaimsPrincipal caller, AppDbContext db, CancellationToken ct)
    {
        var subject = request.Subject?.Trim() ?? string.Empty;
        var body = request.Body?.Trim() ?? string.Empty;

        var errors = new Dictionary<string, string[]>();
        if (subject.Length is 0 or > MaxSubjectLength)
        {
            errors["subject"] = [$"Subject is required and must be at most {MaxSubjectLength} characters."];
        }

        if (body.Length is 0 or > MaxBodyLength)
        {
            errors["body"] = [$"Body is required and must be at most {MaxBodyLength} characters."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        if (!Guid.TryParse(caller.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, out var userId))
        {
            return Results.Problem(title: "Invalid token", statusCode: StatusCodes.Status401Unauthorized);
        }

        var sender = await db.PortalUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (sender is null || !sender.IsActive)
        {
            return Results.Problem(title: "Portal user not found or disabled", statusCode: StatusCodes.Status401Unauthorized);
        }

        if (!await db.Astronauts.AnyAsync(a => a.Id == id, ct))
        {
            return AstronautNotFound();
        }

        var message = new Message
        {
            Id = Guid.CreateVersion7(),
            AstronautId = id,
            Direction = MessageDirection.ToAstronaut,
            Kind = MessageKind.Message,
            Subject = subject,
            Body = body,
            CreatedAt = DateTimeOffset.UtcNow,
            ReceivedAt = null, // set when the astronaut's next sync delivers it
            SenderUserId = userId,
        };
        db.Messages.Add(message);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/mc/v1/crew/{id}/messages", ToDto(message, sender.DisplayName));
    }

    private static async Task<IResult> MarkReadAsync(Guid id, AppDbContext db, CancellationToken ct)
    {
        var message = await db.Messages.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (message is null)
        {
            return Results.Problem(title: "Message not found", statusCode: StatusCodes.Status404NotFound);
        }

        // "Read" is Mission Control reading what an astronaut sent; replies are read on the desktop.
        if (message.Direction != MessageDirection.ToMissionControl)
        {
            return Results.Problem(
                title: "Only messages from astronauts can be marked as read",
                statusCode: StatusCodes.Status409Conflict);
        }

        if (message.ReadAt is null)
        {
            message.ReadAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        return Results.NoContent();
    }

    private static string SenderName(Message m, string astronautName, IReadOnlyDictionary<Guid, string> senders)
        => m.Direction == MessageDirection.ToMissionControl
            ? astronautName
            : m.SenderUserId is { } by ? senders.GetValueOrDefault(by, "Mission Control") : "Mission Control";

    private static MessageDto ToDto(Message m, string? senderName) => new(
        m.Id, m.Direction, m.Kind, m.CheckUpId, m.Subject, m.Body, m.CreatedAt, m.ReceivedAt, m.ReadAt, senderName);

    private static IResult AstronautNotFound() => Results.Problem(title: "Astronaut not found", statusCode: StatusCodes.Status404NotFound);
}
