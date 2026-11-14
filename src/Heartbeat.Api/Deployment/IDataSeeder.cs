namespace Heartbeat.Api.Deployment;

/// <summary>
/// Fills a fresh database with demo data (roles, users, astronauts, check-ups). Implementations are registered in DI
/// and run once at start-up, after migrations, only when <c>Seed:Enabled</c> is true (environment variable
/// <c>Seed__Enabled=true</c>), and must be idempotent because the app may restart many times.
/// </summary>
public interface IDataSeeder
{
    Task SeedAsync(IServiceProvider services, CancellationToken cancellationToken);
}
