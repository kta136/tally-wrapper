namespace ShowroomBilling.Application.Bills;

public sealed class BillStateConflictException(string message) : InvalidOperationException(message);

public sealed class TallyPreflightUnavailableException(string message) : InvalidOperationException(message);

public sealed class BillNotFoundException(Guid billId) : Exception($"Bill '{billId}' was not found.");

public sealed class BillValidationException : Exception
{
    public IReadOnlyList<string> Errors { get; }

    public BillValidationException(IEnumerable<string> errors)
        : this(errors.ToArray())
    {
    }

    private BillValidationException(string[] errors)
        : base(FormatMessage(errors))
    {
        Errors = errors;
    }

    private static string FormatMessage(string[] list)
    {
        return list.Length switch
        {
            0 => "Bill payload is invalid.",
            1 => list[0],
            _ => "Bill payload is invalid: " + string.Join("; ", list)
        };
    }
}
