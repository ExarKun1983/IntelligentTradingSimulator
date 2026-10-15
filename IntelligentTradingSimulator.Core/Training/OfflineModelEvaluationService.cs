using System.Text.Json;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;

namespace IntelligentTradingSimulator.Core.Training;

/// <summary>Evaluates a fixed offline model using held-out features, never labels as scoring inputs.</summary>
public sealed class OfflineModelEvaluationService(
    AiScoringEngine model,
    StrategyConfiguration baselineConfiguration)
{
    public OfflineEvaluationReport Evaluate(TrainingDataset dataset, OfflineEvaluationRequest request)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(baselineConfiguration);
        var query = dataset.Query;
        if (string.IsNullOrWhiteSpace(request.ModelId) || request.TopCount <= 0)
            throw new ArgumentException("A model ID and positive top selection count are required.", nameof(request));
        if (query.From > query.To || query.From <= request.TrainingCutoff || query.To > request.OutcomeCutoff)
            throw new ArgumentException("Evaluation must start after training and end on or before the outcome cutoff.", nameof(request));
        if (query.HorizonTradingDays is null or <= 0 ||
            string.IsNullOrWhiteSpace(query.BenchmarkSymbol) || string.IsNullOrWhiteSpace(query.StrategyId))
            throw new ArgumentException("Select one label horizon, benchmark, and strategy.", nameof(dataset));
        if (dataset.SchemaVersion != 1)
            throw new ArgumentException("Unsupported dataset schema.", nameof(dataset));

        var selected = dataset.Rows.Where(row =>
                row.AnalysisDate >= query.From && row.AnalysisDate <= query.To &&
                row.StrategyId == query.StrategyId &&
                (query.Symbol is null || row.Symbol == query.Symbol) &&
                (query.BacktestRunId is null || row.BacktestRunId == query.BacktestRunId) &&
                row.HorizonTradingDays == query.HorizonTradingDays &&
                row.BenchmarkSymbol == query.BenchmarkSymbol &&
                row.OutcomeDate <= request.OutcomeCutoff)
            .OrderBy(row => row.AnalysisDate)
            .ThenBy(row => row.BacktestRunId)
            .ThenBy(row => row.Symbol, StringComparer.Ordinal)
            .ToList();

        // Validate the entire cohort before calling either scorer.
        foreach (var row in selected)
        {
            if (row.FeatureSchemaVersion != 1 || row.OutcomeDate <= row.AnalysisDate ||
                row.Features.AnalysisDate != row.AnalysisDate ||
                !string.Equals(row.Features.Symbol.Trim(), row.Symbol.Trim(), StringComparison.OrdinalIgnoreCase) ||
                row.Features.Fundamentals?.AsOfDate > row.AnalysisDate)
                throw new ArgumentException("Invalid or future-dated feature/label data.", nameof(dataset));
        }
        if (selected.GroupBy(row => (row.AnalysisDate, row.BacktestRunId, Symbol: row.Symbol.Trim().ToUpperInvariant()))
            .Any(group => group.Count() > 1))
            throw new ArgumentException("Duplicate symbols in an evaluation cohort.", nameof(dataset));

        var baseline = new CompositeScoringEngine(baselineConfiguration);
        var rows = selected.Select(row => new OfflineEvaluationRow(
            row.FeatureSnapshotId, row.FutureOutcomeLabelId, row.Symbol, row.AnalysisDate,
            row.BacktestRunId, row.OutcomeDate, row.ReturnPercent,
            model.CalculateScore(row.Features.ToScoringContext()),
            baseline.CalculateScore(row.Features.ToScoringContext()))).ToList().AsReadOnly();

        return new OfflineEvaluationReport
        {
            Query = query,
            Request = request,
            BaselineConfigurationJson = JsonSerializer.Serialize(baselineConfiguration),
            ExcludedRowCount = dataset.Rows.Count - rows.Count,
            Model = CalculateMetrics(rows, row => row.ModelScore, request.TopCount),
            Baseline = CalculateMetrics(rows, row => row.BaselineScore, request.TopCount),
            Rows = rows
        };
    }

    private static OfflineEvaluationMetrics CalculateMetrics(
        IReadOnlyList<OfflineEvaluationRow> rows, Func<OfflineEvaluationRow, decimal> score, int topCount)
    {
        var eligible = rows.Where(row => score(row) != StrategyConfiguration.DisqualifiedScore).ToList();
        var correlations = new List<double>();
        var selections = new List<OfflineEvaluationRow>();
        foreach (var group in eligible.GroupBy(row => (row.AnalysisDate, row.BacktestRunId)))
        {
            var cohort = group.ToList();
            var correlation = RankCorrelation(cohort.Select(score).ToArray(), cohort.Select(row => row.ReturnPercent).ToArray());
            if (correlation.HasValue)
                correlations.Add(correlation.Value);
            selections.AddRange(cohort.OrderByDescending(score)
                .ThenBy(row => row.Symbol, StringComparer.Ordinal).Take(topCount));
        }

        return new OfflineEvaluationMetrics(
            eligible.Count,
            eligible.Count == 0 ? null : 100m * eligible.Count(row => Math.Sign(score(row)) == Math.Sign(row.ReturnPercent)) / eligible.Count,
            correlations.Count == 0 ? null : correlations.Average(),
            correlations.Count,
            selections.Count == 0 ? null : selections.Average(row => row.ReturnPercent),
            selections.Count);
    }

    private static double? RankCorrelation(decimal[] scores, decimal[] returns)
    {
        if (scores.Length < 2)
            return null;
        var x = AverageRanks(scores);
        var y = AverageRanks(returns);
        var mean = (scores.Length + 1d) / 2d;
        double covariance = 0, varianceX = 0, varianceY = 0;
        for (var i = 0; i < x.Length; i++)
        {
            covariance += (x[i] - mean) * (y[i] - mean);
            varianceX += (x[i] - mean) * (x[i] - mean);
            varianceY += (y[i] - mean) * (y[i] - mean);
        }
        return varianceX == 0 || varianceY == 0
            ? null : Math.Clamp(covariance / Math.Sqrt(varianceX * varianceY), -1d, 1d);
    }

    private static double[] AverageRanks(decimal[] values)
    {
        var indices = Enumerable.Range(0, values.Length).OrderBy(i => values[i]).ToArray();
        var ranks = new double[values.Length];
        for (var start = 0; start < indices.Length;)
        {
            var end = start + 1;
            while (end < indices.Length && values[indices[start]] == values[indices[end]])
                end++;
            var rank = (start + 1d + end) / 2d;
            for (var i = start; i < end; i++)
                ranks[indices[i]] = rank;
            start = end;
        }
        return ranks;
    }
}
