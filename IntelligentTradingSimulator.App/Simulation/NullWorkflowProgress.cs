using System;

namespace IntelligentTradingSimulator.App.Simulation;

public sealed class NullWorkflowProgress : IWorkflowProgress
{
    private static readonly WorkflowProgressContext EmptyContext = new();
    public static NullWorkflowProgress Instance { get; } = new();

    private NullWorkflowProgress() { }

    public WorkflowProgressContext Context
    {
        get => EmptyContext;
        set { }
    }

    public void Report(string message, bool force = false) { }

    public IDisposable BeginOperation(string operation, string? symbol = null) => EmptyScope.Instance;

    private sealed class EmptyScope : IDisposable
    {
        public static EmptyScope Instance { get; } = new();
        public void Dispose() { }
    }
}
