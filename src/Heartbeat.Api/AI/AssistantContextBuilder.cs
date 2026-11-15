using Heartbeat.Api.Crew;
using Heartbeat.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Api.AI;

public sealed class AssistantContextBuilder(AppDbContext db, TimeProvider clock)
{
    public async Task<AssistantContext?> BuildAsync(Guid astronautId, CancellationToken cancellationToken)
    {
        var astronaut = await db.Astronauts.AsNoTracking()
            .Where(a => a.Id == astronautId)
            .Select(a => new { a.Id, a.DisplayName, MissionName = a.Mission.Name })
            .FirstOrDefaultAsync(cancellationToken);

        if (astronaut is null)
        {
            return null;
        }

        var risk = await RiskStatuses.LoadAsync(db, astronautId, 0m, 0m, cancellationToken);
        var alerts = await db.Alerts.AsNoTracking()
            .Where(a => a.AstronautId == astronautId && a.AcknowledgedAt == null)
            .Select(a => new AssistantAlertContext(a.Id, a.Kind, a.Indicators, a.Message, a.CreatedAt))
            .ToListAsync(cancellationToken);

        return new AssistantContext(
            astronaut.Id,
            astronaut.DisplayName,
            astronaut.MissionName,
            clock.GetUtcNow(),
            risk,
            alerts.OrderByDescending(a => a.CreatedAt).Take(10).ToList());
    }
}
