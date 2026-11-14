using System.Net;
using System.Net.Http.Json;
using Heartbeat.Api.Data;
using Heartbeat.Api.Deployment;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Heartbeat.Tests;

public class DeploymentTests
{
    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "t";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static IConfiguration Config(string? key)
        => new ConfigurationBuilder().AddInMemoryCollection(key is null ? [] : new Dictionary<string, string?> { ["Jwt:Key"] = key }).Build();

    [Fact]
    public void Development_falls_back_to_the_dev_key()
        => Assert.Equal(DeploymentSettings.DevelopmentJwtKey, DeploymentSettings.ResolveJwtKey(Config(null), new Env("Development")));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("too-short")]
    [InlineData("dev-only-signing-key-change-me-please-32chars")]
    public void Production_refuses_a_missing_short_or_public_key(string? key)
        => Assert.Throws<InvalidOperationException>(() => DeploymentSettings.ResolveJwtKey(Config(key), new Env("Production")));

    [Fact]
    public void Production_accepts_a_configured_strong_key()
    {
        var key = new string('k', 48);
        Assert.Equal(key, DeploymentSettings.ResolveJwtKey(Config(key), new Env("Production")));
    }

    [Fact]
    public void Relative_sqlite_path_is_anchored_at_the_content_root_and_its_folder_created()
    {
        var root = Path.Combine(Path.GetTempPath(), "hb-root-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = DeploymentSettings.ResolveSqliteConnectionString("Data Source=App_Data/heartbeat.db", root);
            var path = new SqliteConnectionStringBuilder(result).DataSource;
            Assert.Equal(Path.GetFullPath(Path.Combine(root, "App_Data", "heartbeat.db")), path);
            Assert.True(Directory.Exists(Path.Combine(root, "App_Data")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Absolute_and_in_memory_sqlite_paths_are_left_alone()
    {
        Assert.Contains(":memory:", DeploymentSettings.ResolveSqliteConnectionString("Data Source=:memory:", "x"));
        var abs = Path.Combine(Path.GetTempPath(), "hb-abs.db");
        Assert.Equal(abs, new SqliteConnectionStringBuilder(DeploymentSettings.ResolveSqliteConnectionString($"Data Source={abs}", "x")).DataSource);
    }

    private sealed class ProdFactory(string? jwtKey, bool seed = false, bool withPasswords = true) : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"hb-{Guid.NewGuid():N}.db");
        public static int SeederRuns;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("ConnectionStrings:DefaultConnection", $"Data Source={DbPath}");
            if (jwtKey is not null) builder.UseSetting("Jwt:Key", jwtKey);
            builder.UseSetting("Seed:Enabled", seed ? "true" : "false");
            if (seed && withPasswords)
            {
                foreach (var name in Heartbeat.Api.Data.Seeding.DemoDataSeeder.AccountNames)
                {
                    builder.UseSetting($"Seed:Passwords:{name}", "Test-Password-1");
                }
            }
            builder.ConfigureServices(s => s.AddSingleton<IDataSeeder, CountingSeeder>());
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            SqliteConnection.ClearAllPools();
            try { File.Delete(DbPath); } catch (IOException) { }
        }
    }

    private sealed class CountingSeeder : IDataSeeder
    {
        public Task SeedAsync(IServiceProvider services, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref ProdFactory.SeederRuns);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void App_does_not_start_in_production_without_a_jwt_key()
    {
        using var factory = new ProdFactory(jwtKey: null);
        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
    }

    [Fact]
    public async Task Health_is_anonymous_and_reports_ok_in_production()
    {
        using var factory = new ProdFactory(new string('k', 48));
        var res = await factory.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("\"status\":\"ok\"", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public void Seed_without_configured_passwords_stops_the_app_instead_of_using_defaults()
    {
        using var factory = new ProdFactory(new string('k', 48), seed: true, withPasswords: false);
        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
    }

    [Fact]
    public void Seeders_run_only_when_enabled()
    {
        var before = ProdFactory.SeederRuns;
        using (var off = new ProdFactory(new string('k', 48), seed: false)) { off.CreateClient(); }
        Assert.Equal(before, ProdFactory.SeederRuns);

        using (var on = new ProdFactory(new string('k', 48), seed: true)) { on.CreateClient(); }
        Assert.Equal(before + 1, ProdFactory.SeederRuns);
    }
}
