using ShowroomBilling.Contracts.Bills;

namespace ShowroomBilling.Desktop.ViewModels.Bills;

/// <summary>
/// How a bill state is shown: the chip's word, its colour tone and the shape drawn before the
/// word. The Bills list and the Bill Details dialog both use this, so the two cannot drift, and the
/// XAML keys its colours and shapes on the tone and kind rather than on state strings. Colour is
/// never the only signal: every state has its own word and its own shape.
/// </summary>
internal static class BillStateDisplay
{
    public const string ToneOk = "ok";
    public const string ToneWarn = "warn";
    public const string ToneErr = "err";
    public const string ToneInfo = "info";
    public const string ToneNeutral = "neutral";

    /// <summary>Tone for the shared <c>ToneChip</c> and <c>StateChip</c> styles.</summary>
    public static string Tone(string? state) => state switch
    {
        BillStates.Posted => ToneOk,
        BillStates.Pending or BillStates.Posting => ToneWarn,
        BillStates.Failed or BillStates.ReconciliationRequired => ToneErr,
        BillStates.Draft => ToneInfo,
        _ => ToneNeutral
    };

    /// <summary>
    /// The shape before the word: a ring is a draft, a half-filled circle is waiting for Tally
    /// (pending or posting), a dot is posted, a cross is failed, a bang is reconcile, a
    /// circling arrow is revised and a dash is voided.
    /// </summary>
    public static string Kind(string? state) => state switch
    {
        BillStates.Draft => "ring",
        BillStates.Pending or BillStates.Posting => "half",
        BillStates.Posted => "dot",
        BillStates.Failed => "x",
        BillStates.ReconciliationRequired => "bang",
        BillStates.Revised => "redo",
        BillStates.Voided => "dash",
        _ => "none"
    };

    /// <summary>The chip's word, with " · edit" after it for a bill changed since it was pushed.</summary>
    public static string Label(string? state, bool editedAfterPush)
    {
        var basic = state switch
        {
            BillStates.Pending => "Pending",
            BillStates.Draft => "Draft",
            BillStates.Posting => "Posting",
            BillStates.Posted => "Posted",
            BillStates.Failed => "Failed",
            BillStates.ReconciliationRequired => "Reconcile",
            BillStates.Revised => "Revised",
            BillStates.Voided => "Voided",
            null or "" => "—",
            _ => state
        };
        return editedAfterPush ? $"{basic} · edit" : basic;
    }
}
