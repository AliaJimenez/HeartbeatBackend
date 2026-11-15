using Heartbeat.Shared;

namespace Heartbeat.Tests;

public sealed class IndicatorsCatalogValidationTests
{
    [Fact]
    public void Catalog_has_at_least_two_indicators_contrasted_with_open_NASA_data()
    {
        var validated = IndicatorsCatalog.Cached
            .Where(entry => entry.Validation is not null)
            .ToList();

        Assert.True(validated.Count >= 2);
        Assert.Contains(validated, entry => entry.Id == "iop");
        Assert.Contains(validated, entry => entry.Id == "radiationRate");
    }

    [Fact]
    public void NASA_validations_are_traceable_and_do_not_overstate_the_evidence()
    {
        var validated = IndicatorsCatalog.Cached
            .Where(entry => entry.Validation is not null)
            .ToList();

        foreach (var entry in validated)
        {
            var validation = Assert.IsType<IndicatorValidation>(entry.Validation);

            Assert.NotEmpty(validation.Datasets);
            Assert.All(validation.Datasets, dataset =>
            {
                Assert.Matches("^(OSD-\\d+|RR-\\d+|Radi-N2)$", dataset.Accession);
                var expectedSourcePrefix = dataset.Accession switch
                {
                    var accession when accession.StartsWith("OSD-", StringComparison.Ordinal) => "osdr-",
                    var accession when accession.StartsWith("RR-", StringComparison.Ordinal) => "lsda-",
                    _ => "asc-csa-",
                };
                Assert.StartsWith(expectedSourcePrefix, dataset.Source);
                Assert.False(string.IsNullOrWhiteSpace(dataset.Organism));
                Assert.False(string.IsNullOrWhiteSpace(dataset.Finding));
            });

            if (validation.Result != "consistent")
            {
                Assert.Equal(CatalogStatus.Heuristic, entry.Status);
                Assert.False(string.IsNullOrWhiteSpace(validation.DoesNotSupport));
            }
        }
    }

    [Fact]
    public void NASA_source_links_are_https()
    {
        var validated = IndicatorsCatalog.Cached
            .Where(entry => entry.Validation is not null)
            .SelectMany(entry => entry.Validation!.Datasets)
            .Select(dataset => dataset.Source)
            .Distinct()
            .ToList();

        using var catalog = IndicatorsCatalog.Parse();
        var sources = catalog.RootElement.GetProperty("sources");

        Assert.All(validated, sourceId =>
        {
            var url = sources.GetProperty(sourceId).GetProperty("url").GetString();
            Assert.True(Uri.TryCreate(url, UriKind.Absolute, out var uri));
            Assert.Equal(Uri.UriSchemeHttps, uri.Scheme);
        });
    }
}
