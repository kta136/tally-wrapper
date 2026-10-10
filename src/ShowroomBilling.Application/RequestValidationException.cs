namespace ShowroomBilling.Application;

/// <summary>
/// A request failed validation. The message is written for the operator and
/// is returned as the detail of the 400 response.
/// </summary>
/// <remarks>
/// Derives from <see cref="ArgumentException"/> so callers that catch that
/// keep working, but it never carries a parameter name: .NET appends
/// <c> (Parameter 'x')</c> to the message of an ArgumentException that has one.
/// </remarks>
public sealed class RequestValidationException : ArgumentException
{
    public RequestValidationException(string message)
        : base(message)
    {
    }

    public RequestValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
