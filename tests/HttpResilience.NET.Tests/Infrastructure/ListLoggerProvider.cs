using Microsoft.Extensions.Logging;

namespace HttpResilience.NET.Tests.Infrastructure;

/// <summary>One captured log record, with the fields an alert rule keys on.</summary>
internal sealed record LogEntry(string Category, LogLevel Level, EventId EventId, string Message)
{
    public override string ToString() => $"[{Level}] {Category}: {Message}";
}

/// <summary>
/// Captures log records, so a test can assert what an operator would see, including the event ID.
/// </summary>
internal sealed class ListLoggerProvider : ILoggerProvider
{
    private readonly List<LogEntry> _entries = [];

    /// <summary>Every record, formatted as <c>[Level] Category: Message</c>.</summary>
    public IReadOnlyList<string> Records => [.. Entries.Select(entry => entry.ToString())];

    /// <summary>Every record, structured.</summary>
    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_entries)
            {
                return [.. _entries];
            }
        }
    }

    /// <summary>The records with this event ID in this category, in the order they were logged.</summary>
    public IReadOnlyList<LogEntry> WithEvent(string category, int eventId) =>
        [.. Entries.Where(entry => entry.Category == category && entry.EventId.Id == eventId)];

    public ILogger CreateLogger(string categoryName) => new Recorder(this, categoryName);

    public void Dispose()
    {
    }

    private void Add(LogEntry entry)
    {
        lock (_entries)
        {
            _entries.Add(entry);
        }
    }

    private sealed class Recorder(ListLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            provider.Add(new LogEntry(category, logLevel, eventId, formatter(state, exception)));
    }
}
