namespace ShowroomBilling.Desktop.Shell;

/// <summary>
/// Decides what an error nobody caught does to the running app. One such
/// error used to close it, and the bill on screen went with it. Now the app
/// says what happened and stays open, so the operator can look at the bill
/// and save it. It still closes when it has not finished starting (there is
/// nothing to keep), and when errors keep coming: something is then broken
/// in a way that staying open will not help, such as a screen that fails
/// every time it is drawn.
/// </summary>
public sealed class UnhandledErrorPolicy(int errorsAllowed, TimeSpan within)
{
    private readonly Queue<DateTimeOffset> _recent = new();

    /// <summary>Three errors inside a minute are survived; a fourth closes the app.</summary>
    public static UnhandledErrorPolicy Default() => new(errorsAllowed: 3, within: TimeSpan.FromMinutes(1));

    /// <summary>True when the app should stay open after an error at <paramref name="now"/>.</summary>
    public bool ShouldKeepRunning(bool startedUp, DateTimeOffset now)
    {
        if (!startedUp)
        {
            return false;
        }

        while (_recent.Count > 0 && now - _recent.Peek() > within)
        {
            _recent.Dequeue();
        }

        _recent.Enqueue(now);
        return _recent.Count <= errorsAllowed;
    }
}
