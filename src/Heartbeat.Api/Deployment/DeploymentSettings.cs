using Microsoft.Data.Sqlite;

namespace Heartbeat.Api.Deployment;

/// <summary>Environment-dependent settings that must come from outside the repo when the API is deployed.</summary>
public static class DeploymentSettings
{
    public const string DevelopmentJwtKey = "dev-only-signing-key-change-me-please-32chars";
    private const int MinJwtKeyLength = 32;

    /// <summary>
    /// The JWT signing key. Development falls back to a well-known key; any other environment must supply
    /// <c>Jwt:Key</c> (environment variable <c>Jwt__Key</c>) or the app refuses to start, so a deployed API can never
    /// sign tokens with a key that is public in the repository.
    /// </summary>
    public static string ResolveJwtKey(IConfiguration configuration, IHostEnvironment environment)
    {
        var key = configuration["Jwt:Key"];
        if (string.IsNullOrWhiteSpace(key))
        {
            if (environment.IsDevelopment())
            {
                return DevelopmentJwtKey;
            }

            throw new InvalidOperationException(
                "Jwt:Key is not configured. Set the Jwt__Key environment variable (at least 32 characters) on the host.");
        }

        if (!environment.IsDevelopment() && (key.Length < MinJwtKeyLength || key == DevelopmentJwtKey))
        {
            throw new InvalidOperationException(
                $"Jwt:Key must be at least {MinJwtKeyLength} characters and different from the development key.");
        }

        return key;
    }

    /// <summary>
    /// Anchors a relative SQLite file path at the content root (IIS does not guarantee the working directory) and
    /// creates its folder, so <c>Data Source=App_Data/heartbeat.db</c> works on first start.
    /// </summary>
    public static string ResolveSqliteConnectionString(string? connectionString, string contentRoot)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString ?? "Data Source=heartbeat.db");
        var source = builder.DataSource;
        if (string.IsNullOrEmpty(source) || source == ":memory:" || source.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return builder.ConnectionString;
        }

        if (!Path.IsPathRooted(source))
        {
            builder.DataSource = Path.GetFullPath(Path.Combine(contentRoot, source));
        }

        var directory = Path.GetDirectoryName(builder.DataSource);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return builder.ConnectionString;
    }
}
