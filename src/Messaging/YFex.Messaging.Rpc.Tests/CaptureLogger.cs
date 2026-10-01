using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace YFex.Messaging.Rpc.Tests;

/// <summary>Collects log lines (RPC diagnostics in failing tests).</summary>
public sealed class CaptureLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
    public void Dispose() { }

    private sealed class Logger(CaptureLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => owner.Lines.Enqueue($"[{logLevel}] {category}: {formatter(state, exception)}{(exception is null ? "" : "\n" + exception)}");
    }
}
