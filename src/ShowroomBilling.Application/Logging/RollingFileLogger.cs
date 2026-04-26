using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ShowroomBilling.Application.Logging;

public sealed class RollingFileLogger(
    string category,
    RollingFileLoggerProvider provider) : ILogger
{
    public IDisposable BeginScope<TState>(TState state) where TState : notnull =>
        provider.ScopeProvider.Push(state);

    public bool IsEnabled(LogLevel logLevel) =>
        logLevel >= provider.MinLevel && logLevel != LogLevel.None;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var message = formatter(state, exception);
        if (string.IsNullOrEmpty(message) && exception is null)
        {
            return;
        }

        var sb = new StringBuilder(256);
        sb.Append(DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture));
        sb.Append(' ').Append('[').Append(LevelText(logLevel)).Append(']');

        var scopeText = BuildScopeText();
        if (scopeText.Length > 0)
        {
            sb.Append(' ').Append(scopeText);
        }

        sb.Append(' ').Append(category);
        if (eventId.Id != 0)
        {
            sb.Append(" (").Append(eventId.Id);
            if (!string.IsNullOrEmpty(eventId.Name))
            {
                sb.Append(':').Append(eventId.Name);
            }
            sb.Append(')');
        }
        sb.Append(": ").Append(message);
        if (exception is not null)
        {
            sb.AppendLine();
            sb.Append(exception);
        }

        provider.Enqueue(sb.ToString());
    }

    private string BuildScopeText()
    {
        var pairs = new List<string>();
        provider.ScopeProvider.ForEachScope((scope, list) =>
        {
            if (scope is IEnumerable<KeyValuePair<string, object?>> kvps)
            {
                foreach (var kvp in kvps)
                {
                    if (kvp.Key.StartsWith('{') || kvp.Key == "{OriginalFormat}")
                    {
                        continue;
                    }
                    list.Add($"{kvp.Key}={kvp.Value}");
                }
            }
            else if (scope is not null)
            {
                list.Add(scope.ToString() ?? string.Empty);
            }
        }, pairs);

        return pairs.Count == 0 ? string.Empty : "[" + string.Join(' ', pairs) + "]";
    }

    private static string LevelText(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???"
    };
}
