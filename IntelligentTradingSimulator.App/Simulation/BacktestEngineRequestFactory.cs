using System;
using System.Collections.Generic;
using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.App.Simulation;

public sealed class BacktestEngineRequestFactory
{
    private readonly StrategyConfiguration _selectedStrategyConfiguration;

    public BacktestEngineRequestFactory(StrategyConfiguration selectedStrategyConfiguration)
    {
        _selectedStrategyConfiguration =
            selectedStrategyConfiguration ??
            throw new ArgumentNullException(nameof(selectedStrategyConfiguration));
    }

    public BacktestEngineRequest Create(
        string name,
        DateTime startDate,
        DateTime endDate,
        DateTime historicalStartDate,
        decimal initialCapital,
        IReadOnlyList<string> symbols)
    {
        return new BacktestEngineRequest
        {
            Name = name,
            StrategyId = _selectedStrategyConfiguration.Id,
            StartDate = startDate,
            EndDate = endDate,
            HistoricalStartDate = historicalStartDate,
            InitialCapital = initialCapital,
            Symbols = symbols
        };
    }
}
