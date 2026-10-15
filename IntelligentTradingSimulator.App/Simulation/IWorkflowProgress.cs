using System;

namespace IntelligentTradingSimulator.App.Simulation;

public interface IWorkflowProgress
{
    WorkflowProgressContext Context { get; set; }
    void Report(string message, bool force = false);
    IDisposable BeginOperation(string operation, string? symbol = null);
}
