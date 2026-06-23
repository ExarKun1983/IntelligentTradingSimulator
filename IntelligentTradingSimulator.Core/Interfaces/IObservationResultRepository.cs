using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.Core.Interfaces;

public interface IObservationResultRepository
{
    Task SaveAsync(
        IReadOnlyList<ObservationResult> results);
}