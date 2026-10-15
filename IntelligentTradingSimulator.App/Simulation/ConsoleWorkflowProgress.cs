using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;

namespace IntelligentTradingSimulator.App.Simulation;

public sealed class ConsoleWorkflowProgress : IWorkflowProgress, IDisposable
{
    private readonly object _gate = new();
    private readonly TextWriter _output;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _interval;
    private readonly long _started;
    private readonly ITimer _timer;
    private readonly HashSet<string> _announcedOperations = new(StringComparer.Ordinal);
    private WorkflowProgressContext _context = new();
    private OperationScope? _operation;
    private long? _lastOutput;
    private bool _disposed;
    private bool _heartbeatStopped;

    public ConsoleWorkflowProgress(
        TextWriter? output = null,
        TimeProvider? timeProvider = null,
        TimeSpan? interval = null)
    {
        _output = output ?? TextWriter.Synchronized(Console.Out);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _interval = interval ?? TimeSpan.FromSeconds(5);
        if (_interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), "The progress interval must be positive.");
        }

        _started = _timeProvider.GetTimestamp();
        _timer = _timeProvider.CreateTimer(
            _ => Heartbeat(), null, _interval, _interval);
    }

    public WorkflowProgressContext Context
    {
        get { lock (_gate) { return _context; } }
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_gate) { _context = value; }
        }
    }

    public void Report(string message, bool force = false)
    {
        lock (_gate)
        {
            if (!_disposed && (force || CanWrite()))
            {
                Write(message);
            }
        }
    }

    public IDisposable BeginOperation(string operation, string? symbol = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        lock (_gate)
        {
            var scope = new OperationScope(this, operation, symbol, _timeProvider.GetTimestamp(), _operation);
            if (_disposed)
            {
                scope.Closed = true;
                return scope;
            }

            _operation = scope;
            var firstOccurrence = _announcedOperations.Add(operation);
            try
            {
                if (firstOccurrence || CanWrite())
                {
                    Write("Starting " + operation);
                }
            }
            catch
            {
                EndOperation(scope);
                if (firstOccurrence)
                {
                    _announcedOperations.Remove(operation);
                }

                throw;
            }

            return scope;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _operation = null;
        }

        _timer.Dispose();
    }

    private void Heartbeat()
    {
        lock (_gate)
        {
            if (_disposed || _heartbeatStopped || _operation is null || !CanWrite())
            {
                return;
            }

            try
            {
                Write("Still waiting for completion");
                return;
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                _heartbeatStopped = true;
            }
        }

        _timer.Dispose();
    }

    private bool CanWrite() => !_lastOutput.HasValue
        || _timeProvider.GetElapsedTime(_lastOutput.Value) >= _interval;

    private void Write(string message)
    {
        var now = _timeProvider.GetTimestamp();
        var parts = new List<string> { message };
        Add(parts, "Workflow", _context.Workflow);
        Add(parts, "Strategy", _context.StrategyId);
        Add(parts, "Run", _context.RunId?.ToString(CultureInfo.InvariantCulture));
        Add(parts, "Date", _context.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        AddProgress(parts, "Completed calendar dates", _context.CompletedDates, _context.TotalDates);
        AddProgress(parts, "Symbols", _context.CompletedSymbols, _context.TotalSymbols);
        AddProgress(parts, "Variant", _context.VariantIndex, _context.TotalVariants);
        if (_operation is not null)
        {
            Add(parts, "Operation", _operation.Operation);
            Add(parts, "Symbol", _operation.Symbol);
            Add(parts, "Operation elapsed", FormatElapsed(_operation.Started, now));
        }

        Add(parts, "Workflow elapsed", FormatElapsed(_started, now));
        _output.WriteLine(string.Join(" | ", parts).Replace('\r', ' ').Replace('\n', ' '));
        _lastOutput = now;
    }

    private string FormatElapsed(long started, long now) =>
        _timeProvider.GetElapsedTime(started, now).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s";

    private static void Add(List<string> parts, string label, string? value)
    {
        if (value is not null)
        {
            parts.Add(label + ": " + value);
        }
    }

    private static void AddProgress(List<string> parts, string label, int? completed, int? total)
    {
        if (!completed.HasValue && !total.HasValue)
        {
            return;
        }

        var count = completed?.ToString(CultureInfo.InvariantCulture) ?? "?";
        if (total.HasValue)
        {
            count += "/" + total.Value.ToString(CultureInfo.InvariantCulture);
        }

        if (completed.HasValue && total > 0)
        {
            count += " (" + (100d * completed.Value / total.Value).ToString("0.0", CultureInfo.InvariantCulture) + "%)";
        }

        Add(parts, label, count);
    }

    private void EndOperation(OperationScope scope)
    {
        lock (_gate)
        {
            if (scope.Closed)
            {
                return;
            }

            scope.Closed = true;
            var current = _operation;
            while (current is not null && !ReferenceEquals(current, scope))
            {
                current = current.Parent;
            }

            if (current is null)
            {
                return;
            }

            while (_operation is not null && !ReferenceEquals(_operation, scope))
            {
                _operation.Closed = true;
                _operation = _operation.Parent;
            }

            while (_operation is { Closed: true })
            {
                _operation = _operation.Parent;
            }
        }
    }

    private sealed class OperationScope(
        ConsoleWorkflowProgress owner,
        string operation,
        string? symbol,
        long started,
        OperationScope? parent) : IDisposable
    {
        public string Operation { get; } = operation;
        public string? Symbol { get; } = symbol;
        public long Started { get; } = started;
        public OperationScope? Parent { get; } = parent;
        public bool Closed { get; set; }
        public void Dispose() => owner.EndOperation(this);
    }
}
