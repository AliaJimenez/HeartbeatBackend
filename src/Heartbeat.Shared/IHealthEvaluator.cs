namespace Heartbeat.Shared;

public interface IHealthEvaluator
{
    string RangesVersion { get; }

    ReadingEvaluation Evaluate(ReadingInput input);

    CheckUpEvaluation Evaluate(IEnumerable<ReadingInput> inputs);

    IReadOnlyList<string> Validate(ReadingInput input);
}