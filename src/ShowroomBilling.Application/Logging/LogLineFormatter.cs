using System.Globalization;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Display;
using Serilog.Parsing;

namespace ShowroomBilling.Application.Logging;

/// <summary>
/// One log line: <c>{ISO timestamp} [{LEVEL3}] [{scope key=values}] {Category} ({event id}): {message}</c>,
/// then the exception on the following lines. The scope block carries what
/// <c>BeginScope</c> added (the API's <c>CorrelationId</c> among them) and is
/// left out when there is nothing in it.
/// </summary>
internal sealed class LogLineFormatter : ITextFormatter
{
    private const string SourceContextProperty = "SourceContext";
    private const string EventIdProperty = "EventId";
    private const string ScopeProperty = "Scope";

    // Renders the message with string values unquoted, as Microsoft.Extensions.Logging does.
    private static readonly MessageTemplateTextFormatter Message = new("{Message:l}", CultureInfo.InvariantCulture);

    public void Format(LogEvent logEvent, TextWriter output)
    {
        output.Write(logEvent.Timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture));
        output.Write(" [");
        output.Write(LevelText(logEvent.Level));
        output.Write(']');

        WriteScope(logEvent, output);

        if (logEvent.Properties.TryGetValue(SourceContextProperty, out var category))
        {
            output.Write(' ');
            WriteValue(category, output);
        }

        WriteEventId(logEvent, output);

        output.Write(": ");
        Message.Format(logEvent, output);
        output.WriteLine();

        if (logEvent.Exception is not null)
        {
            output.WriteLine(logEvent.Exception.ToString());
        }
    }

    private static void WriteScope(LogEvent logEvent, TextWriter output)
    {
        HashSet<string>? inMessage = null;
        var any = false;

        foreach (var property in logEvent.Properties)
        {
            if (property.Key is SourceContextProperty or EventIdProperty or ScopeProperty)
            {
                continue;
            }

            // A value the message already shows is not repeated.
            inMessage ??= MessagePropertyNames(logEvent);
            if (inMessage.Contains(property.Key))
            {
                continue;
            }

            output.Write(any ? " " : " [");
            any = true;
            output.Write(property.Key);
            output.Write('=');
            WriteValue(property.Value, output);
        }

        // Scopes opened with plain text rather than key/value pairs.
        if (logEvent.Properties.TryGetValue(ScopeProperty, out var scope) && scope is SequenceValue items)
        {
            foreach (var item in items.Elements)
            {
                output.Write(any ? " " : " [");
                any = true;
                WriteValue(item, output);
            }
        }

        if (any)
        {
            output.Write(']');
        }
    }

    private static HashSet<string> MessagePropertyNames(LogEvent logEvent)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in logEvent.MessageTemplate.Tokens)
        {
            if (token is PropertyToken property)
            {
                names.Add(property.PropertyName);
            }
        }
        return names;
    }

    private static void WriteEventId(LogEvent logEvent, TextWriter output)
    {
        if (!logEvent.Properties.TryGetValue(EventIdProperty, out var value) || value is not StructureValue eventId)
        {
            return;
        }

        LogEventPropertyValue? id = null;
        LogEventPropertyValue? name = null;
        foreach (var part in eventId.Properties)
        {
            if (part.Name == "Id")
            {
                id = part.Value;
            }
            else if (part.Name == "Name")
            {
                name = part.Value;
            }
        }

        if (id is null)
        {
            return;
        }

        output.Write(" (");
        WriteValue(id, output);
        if (name is ScalarValue { Value: string { Length: > 0 } })
        {
            output.Write(':');
            WriteValue(name, output);
        }
        output.Write(')');
    }

    private static void WriteValue(LogEventPropertyValue value, TextWriter output)
    {
        if (value is ScalarValue scalar)
        {
            output.Write(Convert.ToString(scalar.Value, CultureInfo.InvariantCulture));
        }
        else
        {
            value.Render(output, format: null, CultureInfo.InvariantCulture);
        }
    }

    private static string LevelText(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => "TRC",
        LogEventLevel.Debug => "DBG",
        LogEventLevel.Information => "INF",
        LogEventLevel.Warning => "WRN",
        LogEventLevel.Error => "ERR",
        LogEventLevel.Fatal => "CRT",
        _ => "???"
    };
}
