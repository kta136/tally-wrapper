using System.Security.Cryptography;
using System.Text;

namespace ShowroomBilling.Infrastructure;

/// <summary>
/// Identity of the showroom every row belongs to. The application runs for a
/// single showroom, addressed by the code <c>default</c>.
/// </summary>
internal static class Showrooms
{
    internal const string DefaultCode = "default";

    /// <summary>
    /// Id of the default showroom. Every table is keyed by it, so the way it
    /// is derived from the code must never change.
    /// </summary>
    internal static readonly Guid DefaultId = IdFor(DefaultCode);

    /// <summary>The stable id for a showroom code: the MD5 of its trimmed, lower-cased text.</summary>
    internal static Guid IdFor(string showroomCode)
    {
        var bytes = Encoding.UTF8.GetBytes(showroomCode.Trim().ToLowerInvariant());
        return new Guid(MD5.HashData(bytes));
    }
}
