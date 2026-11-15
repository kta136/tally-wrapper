namespace ShowroomBilling.Infrastructure.Persistence.Entities;

public sealed class AdminPasscodeEntity
{
    /// <summary>
    /// The id a first passcode is stored under. There is one passcode, and the
    /// primary key is what keeps it one: of two first passcodes set at the
    /// same moment, the second is refused. (A passcode set by an older build
    /// has an id of its own; it is changed in place and never added to.)
    /// </summary>
    public static readonly Guid FirstPasscodeId = new("00000000-0000-0000-0000-000000000001");

    public Guid Id { get; set; }

    public string PasscodeHash { get; set; } = string.Empty;

    public string PasscodeSalt { get; set; } = string.Empty;

    public int Iterations { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}
