using System.Security.Cryptography;
using System.Text;

namespace ShowroomBilling.Infrastructure.Masters;

internal static class MasterSnapshotNormalization
{
    internal static string NormalizeJson(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "{}" : value.Trim();

    internal static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static Guid ResolveShowroomId(string showroomCode) => CreateStableGuid(showroomCode);

    private static Guid CreateStableGuid(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input.Trim().ToLowerInvariant());
        var hash = MD5.HashData(bytes);
        return new Guid(hash);
    }
}
