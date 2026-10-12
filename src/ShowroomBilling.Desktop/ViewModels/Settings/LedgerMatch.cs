using System;
using System.Collections.Generic;

namespace ShowroomBilling.Desktop.ViewModels.Settings;

/// <summary>Whether a ledger named in Settings exists in the list fetched from Tally.</summary>
public enum LedgerMatchState
{
    /// <summary>Nothing to compare: no name is set, or no list has been fetched yet.</summary>
    Unknown,

    /// <summary>The name is in the Tally list.</summary>
    Found,

    /// <summary>The name is not in the list: a misspelling, or a ledger created in Tally after the list was fetched.</summary>
    Missing,
}

public static class LedgerMatch
{
    /// <summary>
    /// Compares ignoring case and the spaces around the name, as Tally does. With no list there is
    /// nothing to say, so the answer is <see cref="LedgerMatchState.Unknown"/> rather than a false alarm.
    /// </summary>
    public static LedgerMatchState Check(string? name, IReadOnlyCollection<string> tallyNames)
    {
        if (string.IsNullOrWhiteSpace(name) || tallyNames.Count == 0)
        {
            return LedgerMatchState.Unknown;
        }

        var wanted = name.Trim();
        foreach (var candidate in tallyNames)
        {
            if (string.Equals(candidate?.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
            {
                return LedgerMatchState.Found;
            }
        }

        return LedgerMatchState.Missing;
    }
}
