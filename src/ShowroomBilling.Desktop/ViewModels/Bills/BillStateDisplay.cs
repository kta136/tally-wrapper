using ShowroomBilling.Contracts.Bills;

namespace ShowroomBilling.Desktop.ViewModels.Bills;

/// <summary>
/// How a bill state is shown: the chip's text and its colour tone. The Bills
/// list and the Bill Details dialog both use this, so the two cannot drift,
/// and the XAML keys its colours on the tone rather than on state strings.
/// </summary>
internal static class BillStateDisplay
{
    public const string ToneOk = "ok";
    public const string ToneWarn = "warn";
    public const string ToneErr = "err";
    public const string ToneInfo = "info";
    public const string ToneNeutral = "neutral";

    /// <summary>Tone for the shared <c>ToneChip</c> style.</summary>
    public static string Tone(string? state) => state switch
    {
        BillStates.Posted => ToneOk,
        BillStates.Pending or BillStates.Posting => ToneWarn,
        BillStates.Failed or BillStates.ReconciliationRequired => ToneErr,
        BillStates.Draft => ToneInfo,
        _ => ToneNeutral
    };

    /// <summary>
    /// Chip text. Leading glyphs match the design bundle's chip iconography
    /// (● filled = posted, ◐ half = pending/posting, ○ empty = draft,
    /// ✕ = failed, ↻ = revised, — em-dash = voided), chosen to line up in a
    /// monospaced font.
    /// </summary>
    public static string Label(string? state, bool editedAfterPush)
    {
        var basic = state switch
        {
            BillStates.Pending => "◐ Pending",
            BillStates.Draft => "○ Draft",
            BillStates.Posting => "◐ Posting",
            BillStates.Posted => "● Posted",
            BillStates.Failed => "✕ Failed",
            BillStates.ReconciliationRequired => "! Reconcile",
            BillStates.Revised => "↻ Revised",
            BillStates.Voided => "— Voided",
            null or "" => "—",
            _ => state
        };
        return editedAfterPush ? $"{basic} · edit" : basic;
    }
}
