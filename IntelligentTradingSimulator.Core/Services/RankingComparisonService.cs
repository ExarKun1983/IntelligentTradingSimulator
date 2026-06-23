using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.Core.Services;

public sealed class RankingComparisonService
{
    public IReadOnlyList<RankChange> Compare(
        ObservationRun currentRun,
        ObservationRun previousRun)
    {
        var result =
            new List<RankChange>();

        foreach (var current in currentRun.Results)
        {
            var previous =
                previousRun.Results
                    .FirstOrDefault(
                        x => x.Symbol == current.Symbol);

            if (previous is null)
            {
                continue;
            }

            result.Add(
                new RankChange
                {
                    Symbol = current.Symbol,

                    PreviousRank =
                        previous.RankPosition,

                    CurrentRank =
                        current.RankPosition
                });
        }

        return result
            .OrderByDescending(x => x.Change)
            .ToList();
    }
}