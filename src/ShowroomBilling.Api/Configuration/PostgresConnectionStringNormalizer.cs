using Npgsql;

namespace ShowroomBilling.Api.Configuration;

public static class PostgresConnectionStringNormalizer
{
    public static bool TryNormalize(string input, out string connectionString, out string error)
    {
        connectionString = string.Empty;
        error = string.Empty;

        var candidate = ExtractCandidate(input);
        if (string.IsNullOrWhiteSpace(candidate))
        {
            error = "PostgreSQL connection string is required.";
            return false;
        }

        if (StartsWithPostgresUri(candidate))
        {
            return TryNormalizeUri(candidate, out connectionString, out error);
        }

        try
        {
            connectionString = new NpgsqlConnectionStringBuilder(candidate).ConnectionString;
            return true;
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string ExtractCandidate(string input)
    {
        var value = StripWrappingQuotes(input.Trim());
        var uriIndex = IndexOfPostgresUri(value);
        if (uriIndex >= 0)
        {
            value = value[uriIndex..].Trim();
            var quoteIndex = FirstQuoteIndex(value);
            if (quoteIndex > 0)
            {
                value = value[..quoteIndex];
            }
        }
        else if (value.StartsWith("psql ", StringComparison.OrdinalIgnoreCase))
        {
            value = value[4..].Trim();
        }

        return StripWrappingQuotes(value.Trim());
    }

    private static bool TryNormalizeUri(string input, out string connectionString, out string error)
    {
        connectionString = string.Empty;
        error = string.Empty;

        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri)
            || !StartsWithPostgresUri(uri.Scheme))
        {
            error = "PostgreSQL URI is not valid.";
            return false;
        }

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'))
        };

        if (uri.Port > 0)
        {
            builder.Port = uri.Port;
        }

        if (!string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(':', 2);
            builder.Username = Uri.UnescapeDataString(parts[0]);
            if (parts.Length > 1)
            {
                builder.Password = Uri.UnescapeDataString(parts[1]);
            }
        }

        foreach (var (key, value) in ParseQuery(uri.Query))
        {
            ApplyQueryOption(builder, key, value);
        }

        connectionString = builder.ConnectionString;
        return true;
    }

    private static void ApplyQueryOption(NpgsqlConnectionStringBuilder builder, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        var normalizedKey = key.Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        switch (normalizedKey)
        {
            case "sslmode":
                builder["SSL Mode"] = NormalizeEnumLikeValue(value);
                break;
            case "channelbinding":
                builder["Channel Binding"] = NormalizeEnumLikeValue(value);
                break;
            case "connecttimeout":
            case "timeout":
                if (int.TryParse(value, out var timeout))
                {
                    builder.Timeout = timeout;
                }
                break;
            case "commandtimeout":
                if (int.TryParse(value, out var commandTimeout))
                {
                    builder.CommandTimeout = commandTimeout;
                }
                break;
        }
    }

    private static IEnumerable<(string Key, string Value)> ParseQuery(string query)
    {
        var trimmed = query.TrimStart('?');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            yield break;
        }

        foreach (var pair in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0]);
            var value = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
            yield return (key, value);
        }
    }

    private static string NormalizeEnumLikeValue(string value)
    {
        var parts = value
            .Split(['-', '_', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Concat(parts.Select(part =>
            part.Length == 0
                ? string.Empty
                : char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant()));
    }

    private static int IndexOfPostgresUri(string value)
    {
        var postgresql = value.IndexOf("postgresql://", StringComparison.OrdinalIgnoreCase);
        var postgres = value.IndexOf("postgres://", StringComparison.OrdinalIgnoreCase);
        if (postgresql < 0) return postgres;
        if (postgres < 0) return postgresql;
        return Math.Min(postgresql, postgres);
    }

    private static bool StartsWithPostgresUri(string value) =>
        value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
        || value.Equals("postgresql", StringComparison.OrdinalIgnoreCase)
        || value.Equals("postgres", StringComparison.OrdinalIgnoreCase);

    private static int FirstQuoteIndex(string value)
    {
        var single = value.IndexOf('\'', StringComparison.Ordinal);
        var doubleQuote = value.IndexOf('"', StringComparison.Ordinal);
        if (single < 0) return doubleQuote;
        if (doubleQuote < 0) return single;
        return Math.Min(single, doubleQuote);
    }

    private static string StripWrappingQuotes(string value)
    {
        if (value.Length >= 2
            && ((value[0] == '\'' && value[^1] == '\'')
                || (value[0] == '"' && value[^1] == '"')))
        {
            return value[1..^1];
        }

        return value;
    }
}
