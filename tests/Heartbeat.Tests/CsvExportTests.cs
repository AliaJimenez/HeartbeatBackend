using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Heartbeat.Api.Auth;
using Heartbeat.Api.Crew;
using Heartbeat.Api.Data;
using Heartbeat.Api.Data.Entities;
using Heartbeat.Shared;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using EnvironmentReading = Heartbeat.Shared.EnvironmentReading;

namespace Heartbeat.Tests;

/// <summary>HB-BE-10 (#122): CSV report per astronaut and per mission, behind vitals.history.</summary>
public class CsvExportTests : IDisposable
{
    private sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"hb-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
            => builder.UseSetting("ConnectionStrings:DefaultConnection", $"Data Source={_dbPath}");

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(_dbPath); } catch (IOException) { }
        }
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private const string TrickyName = "=Ana, \"Q\" Rivera";

    private readonly Factory _factory = new();
    private readonly Guid _missionId = Guid.NewGuid();
    private readonly Guid _otherMissionId = Guid.NewGuid();
    private readonly Guid _anaId = Guid.NewGuid();
    private readonly Guid _benId = Guid.NewGuid();
    private readonly Guid _outsiderId = Guid.NewGuid();
    private readonly Guid _surgeonId = Guid.NewGuid();

    public void Dispose() => _factory.Dispose();

    public CsvExportTests()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Missions.AddRange(
            new Mission { Id = _missionId, Name = "Alpha", StartsAt = Now.AddDays(-30) },
            new Mission { Id = _otherMissionId, Name = "Beta", StartsAt = Now.AddDays(-30) });
        var role = new Role { Id = Guid.NewGuid(), Name = "Surgeon" };
        db.Roles.Add(role);
        db.PortalUsers.Add(new PortalUser { Id = _surgeonId, UserName = "j.okafor", Email = "j@x.io", DisplayName = "Dr. Okafor", RoleId = role.Id, IsActive = true });
        db.Astronauts.AddRange(
            new Astronaut { Id = _anaId, UserName = "ana", DisplayName = TrickyName, MissionId = _missionId, Notes = "private profile note", DateOfBirth = new DateOnly(1990, 5, 5), UpdatedAt = Now },
            new Astronaut { Id = _benId, UserName = "ben", DisplayName = "Ben Ortiz", MissionId = _missionId, UpdatedAt = Now },
            new Astronaut { Id = _outsiderId, UserName = "cy", DisplayName = "Cy Outsider", MissionId = _otherMissionId, UpdatedAt = Now });
        db.SaveChanges();
    }

    private HttpClient Authorized(string audience, Guid userId, params string[] permissions)
    {
        var jwt = _factory.Services.GetRequiredService<JwtTokenService>();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", jwt.CreateAccessToken(userId, "Someone", audience, permissions: permissions));
        return client;
    }

    private HttpClient Crew(Guid id) => Authorized(AuthConstants.CrewAudience, id);

    private HttpClient Mc(params string[] permissions) => Authorized(AuthConstants.McAudience, _surgeonId, permissions);

    private HttpClient Exporter() => Mc(Permissions.VitalsHistory);

    private async Task Sync(Guid astronaut, IReadOnlyList<CheckUpDto>? checkUps = null, IReadOnlyList<RadiationDoseDto>? doses = null, IReadOnlyList<EnvironmentReading>? env = null)
    {
        var request = new SyncRequest("dev", null, "2026.11.14-1", null, checkUps ?? [], [], doses, env);
        var res = await Crew(astronaut).PostAsJsonAsync("/api/crew/v1/sync", request, Json);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    private static CheckUpDto CheckUp(DateTimeOffset at, string? note, params (IndicatorType Type, decimal Value, decimal? Second)[] readings)
    {
        var evaluation = new HealthEvaluator().Evaluate(readings.Select(r => new ReadingInput(r.Type, r.Value, r.Second)));
        return new CheckUpDto(
            Guid.NewGuid(), at, null, evaluation.OverallStatus, evaluation.RangesVersion, note, null,
            evaluation.Readings.Select((e, i) => new ReadingDto(Guid.NewGuid(), e.Indicator, readings[i].Value, readings[i].Second, e.Status, e.BandIds)).ToList());
    }

    private async Task SeedAnaAsync()
    {
        await Sync(_anaId,
            [
                CheckUp(Now.AddDays(-2), "secret check-up note", (IndicatorType.BloodPressure, 190, 125), (IndicatorType.HeartRate, 70, null)),
                CheckUp(Now.AddMinutes(-30), null, (IndicatorType.HeartRate, 64, null)),
            ],
            [new RadiationDoseDto(Guid.NewGuid(), Now.AddHours(-5), null, 260m, null, RadiationType.Spe)],
            [
                new EnvironmentReading(Guid.NewGuid(), Now.AddHours(-4), null, "co2", 5.5m),
                new EnvironmentReading(Guid.NewGuid(), Now.AddHours(-3), null, "iop", 22m),
            ]);
    }

    private static async Task<(HttpResponseMessage Response, byte[] Bytes, List<string[]> Rows)> Get(HttpClient http, string url)
    {
        var res = await http.GetAsync(url);
        var bytes = await res.Content.ReadAsByteArrayAsync();
        var text = Encoding.UTF8.GetString(bytes).TrimStart('﻿');
        return (res, bytes, res.IsSuccessStatusCode ? ParseCsv(text) : []);
    }

    /// <summary>A small RFC 4180 reader, so the tests check what a spreadsheet would read.</summary>
    private static List<string[]> ParseCsv(string text)
    {
        var rows = new List<string[]>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') { quoted = false; }
                else { field.Append(c); }
            }
            else if (c == '"') { quoted = true; }
            else if (c == ',') { fields.Add(field.ToString()); field.Clear(); }
            else if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                fields.Add(field.ToString()); field.Clear(); rows.Add([.. fields]); fields.Clear(); i++;
            }
            else { field.Append(c); }
        }

        Assert.Empty(field.ToString());   // the file ends with a line break
        return rows;
    }

    private static string[] Col(IEnumerable<string[]> rows, int column) => rows.Select(r => r[column]).ToArray();

    private static IEnumerable<string[]> Of(List<string[]> rows, string type) => rows.Skip(1).Where(r => r[2] == type);

    // ---- permissions ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("/api/mc/v1/mission/export.csv")]
    [InlineData("/api/mc/v1/crew/{ana}/export.csv")]
    public async Task Both_exports_need_vitals_history(string template)
    {
        var url = template.Replace("{ana}", _anaId.ToString());

        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Mc(Permissions.VitalsView, Permissions.CrewView, Permissions.AlertsView).GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Crew(_anaId).GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Exporter().GetAsync(url)).StatusCode);
    }

    // ---- the file -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_download_is_an_attachment_utf8_with_a_bom_and_crlf_lines_and_the_documented_header()
    {
        var (res, bytes, rows) = await Get(Exporter(), $"/api/mc/v1/crew/{_anaId}/export.csv");

        Assert.Equal("text/csv", res.Content.Headers.ContentType!.MediaType);
        Assert.Equal("utf-8", res.Content.Headers.ContentType.CharSet);
        Assert.Equal("attachment", res.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Matches(@"^heartbeat-ana-\d{8}\.csv$", res.Content.Headers.ContentDisposition.FileName);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3));
        Assert.Contains("\r\n", Encoding.UTF8.GetString(bytes));
        Assert.Equal(CsvExports.Header, rows[0]);
        Assert.All(rows, r => Assert.Equal(CsvExports.Header.Length, r.Length));
    }

    [Fact]
    public async Task An_astronaut_without_data_still_gets_the_risk_summary()
    {
        var (_, _, rows) = await Get(Exporter(), $"/api/mc/v1/crew/{_benId}/export.csv");

        Assert.Equal(new[] {"global", "isolationAndConfinement", "distanceFromEarth", "gravityFields", "hostileClosedEnvironment", "spaceRadiation" }.Order(), Col(Of(rows, "risk"), 4).Order());
        Assert.Empty(Of(rows, "vital"));
    }

    [Fact]
    public async Task The_crew_export_has_vitals_radiation_environment_and_risk_rows_with_units_status_and_source()
    {
        await SeedAnaAsync();

        var (_, _, rows) = await Get(Exporter(), $"/api/mc/v1/crew/{_anaId}/export.csv");

        var vitals = Of(rows, "vital").ToList();
        Assert.Equal(3, vitals.Count);
        var pressure = Assert.Single(vitals, r => r[4] == "bloodPressure");
        Assert.Equal(["190", "125", "mmHg", "critical"], new[] { pressure[5], pressure[6], pressure[7], pressure[8] });
        Assert.False(string.IsNullOrWhiteSpace(pressure[9]));       // the band label
        Assert.False(string.IsNullOrWhiteSpace(pressure[10]));      // the source
        Assert.Equal("false", pressure[12]);

        var dose = Assert.Single(Of(rows, "radiation"));
        Assert.Equal(["radiationDose", "260", "mSv", "spe", "true"], new[] { dose[4], dose[5], dose[7], dose[9], dose[12] });

        var env = Of(rows, "environment").ToList();
        Assert.Equal(2, env.Count);
        var co2 = Assert.Single(env, r => r[4] == "co2");
        Assert.Equal(["5.5", "mmHg", "critical", "heuristic", "true"], new[] { co2[5], co2[7], co2[8], co2[11], co2[12] });
        Assert.Single(env, r => r[4] == "iop" && r[8] == "watch");

        var risk = Of(rows, "risk").ToList();
        Assert.Equal("critical", Assert.Single(risk, r => r[4] == "global")[8]);
        Assert.Equal("critical", Assert.Single(risk, r => r[4] == "spaceRadiation")[8]);
        Assert.All(risk, r => Assert.Equal("true", r[12]));
    }

    [Fact]
    public async Task Rows_are_grouped_by_type_and_ordered_by_time()
    {
        await SeedAnaAsync();

        var (_, _, rows) = await Get(Exporter(), $"/api/mc/v1/crew/{_anaId}/export.csv");

        var types = Col(rows.Skip(1), 2);
        Assert.Equal(types.OrderBy(t => Array.IndexOf(new[] { "risk", "vital", "radiation", "environment" }, t)), types);
        var vitalTimes = Of(rows, "vital").Select(r => r[3]).ToList();
        Assert.Equal(vitalTimes.Order(), vitalTimes);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$", vitalTimes[0]);
    }

    [Fact]
    public async Task Names_with_commas_quotes_and_formulas_are_escaped_so_a_spreadsheet_reads_text()
    {
        var (_, bytes, rows) = await Get(Exporter(), $"/api/mc/v1/crew/{_anaId}/export.csv");

        var names = Col(rows.Skip(1), 1).Distinct().ToList();
        Assert.Equal(["'" + TrickyName], names);                    // quotes and comma round-trip; the leading = is neutralised
        Assert.Contains("\"'=Ana, \"\"Q\"\" Rivera\"", Encoding.UTF8.GetString(bytes));
        Assert.All(rows.Skip(1), r => Assert.False(r[1].StartsWith('=') || r[9].StartsWith('=') || r[10].StartsWith('=')));
    }

    [Fact]
    public async Task Free_text_and_the_date_of_birth_are_never_exported()
    {
        await SeedAnaAsync();

        var (_, bytes, _) = await Get(Exporter(), $"/api/mc/v1/crew/{_anaId}/export.csv");
        var text = Encoding.UTF8.GetString(bytes);

        Assert.DoesNotContain("secret check-up note", text);
        Assert.DoesNotContain("private profile note", text);
        Assert.DoesNotContain("1990", text);
    }

    [Fact]
    public async Task The_period_filters_the_dated_records_but_not_the_risk_summary()
    {
        await SeedAnaAsync();
        var from = Uri.EscapeDataString(Now.AddHours(-1).ToString("O"));

        var (_, _, recent) = await Get(Exporter(), $"/api/mc/v1/crew/{_anaId}/export.csv?from={from}");
        var (_, _, all) = await Get(Exporter(), $"/api/mc/v1/crew/{_anaId}/export.csv");

        Assert.Equal(1, Of(recent, "vital").Count());               // only the check-up of 30 minutes ago
        Assert.Empty(Of(recent, "radiation"));
        Assert.Empty(Of(recent, "environment"));
        Assert.Equal(Of(all, "risk").Count(), Of(recent, "risk").Count());
        Assert.Equal(3, Of(all, "vital").Count());
    }

    [Fact]
    public async Task A_correction_replaces_the_check_up_it_supersedes()
    {
        var original = CheckUp(Now.AddHours(-3), null, (IndicatorType.HeartRate, 150, null));
        var correction = original with
        {
            Id = Guid.NewGuid(), SupersedesId = original.Id, RecordedAt = Now.AddHours(-2),
            Readings = [new ReadingDto(Guid.NewGuid(), IndicatorType.HeartRate, 66, null, HealthStatus.Normal, ["hr-normal"])],
        };
        await Sync(_benId, [original]);
        await Sync(_benId, [correction]);

        var (_, _, rows) = await Get(Exporter(), $"/api/mc/v1/crew/{_benId}/export.csv");

        Assert.Equal("66", Assert.Single(Of(rows, "vital"))[5]);
    }

    [Fact]
    public async Task Unknown_astronaut_is_a_404_and_from_after_to_is_a_400()
    {
        var (missing, _, _) = await Get(Exporter(), $"/api/mc/v1/crew/{Guid.NewGuid()}/export.csv");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var from = Uri.EscapeDataString(Now.ToString("O"));
        var to = Uri.EscapeDataString(Now.AddDays(-1).ToString("O"));
        var (bad, _, _) = await Get(Exporter(), $"/api/mc/v1/crew/{_anaId}/export.csv?from={from}&to={to}");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Exporter().GetAsync($"/api/mc/v1/mission/export.csv?from={from}&to={to}")).StatusCode);
    }

    // ---- mission --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_mission_export_has_every_astronaut_of_the_mission_and_nobody_else()
    {
        await SeedAnaAsync();
        await Sync(_outsiderId, [CheckUp(Now.AddMinutes(-10), null, (IndicatorType.HeartRate, 88, null))]);

        var (res, _, rows) = await Get(Exporter(), "/api/mc/v1/mission/export.csv");

        Assert.Matches(@"^heartbeat-mission-alpha-\d{8}\.csv$", res.Content.Headers.ContentDisposition!.FileName);
        var ids = Col(rows.Skip(1), 0).Distinct().Order().ToArray();
        Assert.Equal(new[] { _anaId.ToString(), _benId.ToString() }.Order(), ids);
        Assert.Equal(3, rows.Skip(1).Count(r => r[0] == _anaId.ToString() && r[2] == "vital"));
        Assert.Equal(6, rows.Skip(1).Count(r => r[0] == _benId.ToString() && r[2] == "risk"));
    }

    [Fact]
    public async Task The_mission_export_can_pick_another_mission_and_a_missing_one_is_a_404()
    {
        var (_, _, rows) = await Get(Exporter(), $"/api/mc/v1/mission/export.csv?missionId={_otherMissionId}");

        Assert.Equal([_outsiderId.ToString()], Col(rows.Skip(1), 0).Distinct());
        Assert.Equal(HttpStatusCode.NotFound, (await Exporter().GetAsync($"/api/mc/v1/mission/export.csv?missionId={Guid.NewGuid()}")).StatusCode);
    }

    // ---- helpers --------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("two\nlines", "\"two\nlines\"")]
    public void Escape_quotes_only_what_needs_it(string input, string expected)
        => Assert.Equal(expected, CsvExports.Escape(input));

    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+cmd", "'+cmd")]
    [InlineData("-5", "'-5")]
    [InlineData("@sum", "'@sum")]
    [InlineData("Ana", "Ana")]
    [InlineData("", "")]
    public void Text_neutralises_formula_starts(string input, string expected)
        => Assert.Equal(expected, CsvExports.Text(input));

    [Fact]
    public void Numbers_use_the_invariant_culture_and_keep_their_sign()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("es-ES");
            Assert.Equal("-0.3", CsvExports.Number(-0.3m));
            Assert.Equal("5.5", CsvExports.Number(5.5m));
            Assert.Equal(string.Empty, CsvExports.Number(null));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void File_names_are_safe_for_a_header()
    {
        var name = CsvExports.FileName("Mission \"A\"/../x y", new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero));

        Assert.Matches(@"^heartbeat-[a-z0-9._-]+-20261115\.csv$", name);
        Assert.Equal("heartbeat-export-20261115.csv", CsvExports.FileName("///", new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero)));
    }
}
