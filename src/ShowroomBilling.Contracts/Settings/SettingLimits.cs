namespace ShowroomBilling.Contracts.Settings;

/// <summary>
/// Limits the server enforces on settings. The Desktop reads the same
/// constants so a value it accepts is one the server will store and use.
/// </summary>
public static class SettingLimits
{
    /// <summary>Shortest Tally request timeout. A shorter one would time out ordinary voucher writes.</summary>
    public const int MinTallyTimeoutSeconds = 5;

    public const int MaxTallyTimeoutSeconds = 300;

    /// <summary>Minimum length of the admin passcode.</summary>
    public const int MinAdminPasscodeLength = 6;
}
