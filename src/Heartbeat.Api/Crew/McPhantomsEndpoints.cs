using Heartbeat.Api.Auth;
using Heartbeat.Shared;

namespace Heartbeat.Api.Crew;

/// <summary>Phantom comparison for Mission Control (HB-BE-08): the same simulated stay for several phantoms.</summary>
public static class McPhantomsEndpoints
{
    public static IEndpointRouteBuilder MapMcPhantomsEndpoints(this IEndpointRouteBuilder app)
    {
        // Catalog-driven simulation with no personal data: any signed-in Mission Control user may read it.
        app.MapGet("/api/mc/v1/phantoms/compare", Compare).RequireAuthorization(AuthConstants.McPolicy);
        return app;
    }

    private static IResult Compare(string? profiles = null, string? environment = null, int? weeks = null, decimal? exerciseHours = null)
    {
        var defaults = PhantomComparison.Defaults;
        var maxWeeks = Phantoms.Model.MaxWeeks;
        var errors = new Dictionary<string, string[]>();

        var chosen = new List<PhantomProfile>();
        if (string.IsNullOrWhiteSpace(profiles))
        {
            chosen.AddRange(Phantoms.Profiles);
        }
        else
        {
            var unknown = new List<string>();
            foreach (var key in profiles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (PhantomComparison.FindPhantom(key) is { } phantom)
                {
                    chosen.Add(phantom);
                }
                else
                {
                    unknown.Add(key);
                }
            }

            if (unknown.Count > 0)
            {
                errors["profiles"] = [$"Unknown phantom(s): {string.Join(", ", unknown)}. Use a name or id such as {string.Join(", ", Phantoms.Profiles.Select(p => p.Name))}."];
            }
            else if (chosen.Count == 0)
            {
                errors["profiles"] = ["profiles must name at least one phantom (or be left out for all of them)."];
            }
        }

        var env = PhantomComparison.FindEnvironment(string.IsNullOrWhiteSpace(environment) ? defaults.Environment : environment);
        if (env is null)
        {
            errors["environment"] = [$"Unknown environment '{environment}'. Use one of: {string.Join(", ", PhantomComparison.Environments.Select(e => e.Key))}, or a profile id such as mining."];
        }

        var stayWeeks = weeks ?? defaults.Weeks;
        if (stayWeeks < 1 || stayWeeks > maxWeeks)
        {
            errors["weeks"] = [$"weeks must be between 1 and {maxWeeks}."];
        }

        var exercise = exerciseHours ?? defaults.ExerciseHoursPerWeek;
        if (exercise < 0 || exercise > defaults.MaxExerciseHoursPerWeek)
        {
            errors["exerciseHours"] = [$"exerciseHours must be between 0 and {defaults.MaxExerciseHoursPerWeek:0.##}."];
        }

        return errors.Count > 0
            ? Results.ValidationProblem(errors)
            : Results.Ok(PhantomComparison.Compare(chosen, env!, stayWeeks, exercise));
    }
}
