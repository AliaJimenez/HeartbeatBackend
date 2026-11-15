namespace Heartbeat.Shared;

public static class RadiationDoses
{
    public const string SourceId = "csa-radiation";

    public const decimal MaxEntryMsv = 500m;

    public const int NoteMaxLength = 200;

    public static IReadOnlyList<string> Validate(decimal doseMsv, string? note)
    {
        var errors = new List<string>();

        if (doseMsv <= 0)
        {
            errors.Add("Radiation dose must be greater than 0 mSv");
        }
        else if (doseMsv > MaxEntryMsv)
        {
            errors.Add($"Radiation dose cannot exceed {MaxEntryMsv:0} mSv per entry");
        }

        if (note is { Length: > NoteMaxLength })
        {
            errors.Add($"Radiation note cannot exceed {NoteMaxLength} characters");
        }

        return errors;
    }

    public static decimal Total(IEnumerable<decimal> dosesMsv) => dosesMsv.Sum();
}
