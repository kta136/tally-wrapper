using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ShowroomBilling.Contracts.Bills;

namespace ShowroomBilling.Desktop.Services;

/// <summary>What reading a link gave: the document, or the one sentence that says why it was refused.</summary>
public sealed record HandoverReadResult(HandoverDocument? Document, string? Problem)
{
    public bool IsAccepted => Document is not null;

    public static HandoverReadResult Refused(string problem) => new(null, problem);
}

/// <summary>
/// Reads a <c>tallywrapper:handover?d=…&amp;s=…</c> link and checks it, in the contract's order:
/// the link's shape, that this PC has a key, the signature, the JSON's shape and limits, its
/// version, and its age. Anything that fails is refused with a sentence for the operator, and
/// nothing in it is used. See docs/18_bill_handover.md.
/// </summary>
public static class HandoverLinkReader
{
    public const string Scheme = "tallywrapper";
    public const int Version = 1;
    public const int MaxBills = 20;
    public const int MaxLines = 100;
    public const int MaxNotSent = 100;
    public const int MaxPartyNameLength = 200;

    /// <summary>A link longer than this is refused before it is decoded. Windows passes at most 32 767 characters anyway.</summary>
    public const int MaxLinkLength = 1024 * 1024;

    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MaxAhead = TimeSpan.FromMinutes(5);

    private const int SignatureLength = 43;
    private const decimal MaxWeight = 100_000m;
    private const decimal MaxMoney = 100_000_000m;

    public const string NotAHandover = "This isn't a hand-over link Tally Wrapper can read.";
    public const string NoKey = "This PC has no hand-over key. Set it in Settings → Hand-over.";
    public const string WrongSignature = "This hand-over isn't signed with this PC's key.";
    public const string Unreadable = "This hand-over can't be read.";
    public const string NewerVersion = "This hand-over is from a newer version. Update Tally Wrapper.";
    public const string TooOld = "This hand-over is too old. Send it again.";
    public const string AheadOfClock = "This hand-over is dated ahead of this PC's clock. Check the clock, then send it again.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        RespectNullableAnnotations = true,
        AllowDuplicateProperties = false,
    };

    /// <summary>True when <paramref name="argument"/> is meant for this reader: it starts with the scheme, in any case.</summary>
    public static bool IsHandoverArgument(string? argument)
        => argument is not null
           && argument.TrimStart().StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase);

    public static HandoverReadResult Read(string? link, byte[]? key, DateTimeOffset now)
    {
        if (!TrySplit(link, out var data, out var signature))
        {
            return HandoverReadResult.Refused(NotAHandover);
        }

        if (key is not { Length: HandoverKeyStore.KeyLength })
        {
            return HandoverReadResult.Refused(NoKey);
        }

        if (!SignatureMatches(data, signature, key))
        {
            return HandoverReadResult.Refused(WrongSignature);
        }

        HandoverDocument? document;
        try
        {
            var json = Base64Url.DecodeFromChars(data);
            var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(json);

            // The version decides the shape, so it is read first: a newer sender's hand-over
            // says so instead of failing the version-1 checks below.
            using (var probe = JsonDocument.Parse(text))
            {
                if (probe.RootElement.ValueKind != JsonValueKind.Object
                    || !probe.RootElement.TryGetProperty("v", out var v)
                    || v.ValueKind != JsonValueKind.Number
                    || !v.TryGetInt32(out var version))
                {
                    return HandoverReadResult.Refused(Unreadable);
                }

                if (version != Version)
                {
                    return HandoverReadResult.Refused(version > Version ? NewerVersion : Unreadable);
                }
            }

            document = JsonSerializer.Deserialize<HandoverDocument>(text, JsonOptions);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or DecoderFallbackException or ArgumentException)
        {
            return HandoverReadResult.Refused(Unreadable);
        }

        if (document is null)
        {
            return HandoverReadResult.Refused(Unreadable);
        }

        if (CheckShape(document) is { } problem)
        {
            return HandoverReadResult.Refused(problem);
        }

        var issuedAt = document.IssuedAt.ToUniversalTime();
        if (now - issuedAt > MaxAge)
        {
            return HandoverReadResult.Refused(TooOld);
        }

        if (issuedAt - now > MaxAhead)
        {
            return HandoverReadResult.Refused(AheadOfClock);
        }

        return new HandoverReadResult(document with { NotSent = document.NotSent ?? [] }, null);
    }

    /// <summary>The signature the contract defines: HMAC-SHA256 over the ASCII of <paramref name="data"/>, base64url without padding.</summary>
    public static string Sign(string data, byte[] key)
        => Base64Url.EncodeToString(HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(data)));

    /// <summary>
    /// Accepts <c>tallywrapper:handover?…</c>, <c>tallywrapper://handover?…</c> and
    /// <c>tallywrapper:handover/?…</c>, the scheme and host in any case, with exactly the
    /// parameters <c>d</c> and <c>s</c>.
    /// </summary>
    private static bool TrySplit(string? link, out string data, out string signature)
    {
        data = string.Empty;
        signature = string.Empty;
        if (string.IsNullOrWhiteSpace(link))
        {
            return false;
        }

        var text = link.Trim();
        if (text.Length > MaxLinkLength || !text.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = text[(Scheme.Length + 1)..];
        if (rest.StartsWith("//", StringComparison.Ordinal))
        {
            rest = rest[2..];
        }

        const string Host = "handover";
        if (!rest.StartsWith(Host, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        rest = rest[Host.Length..];
        if (rest.StartsWith('/'))
        {
            rest = rest[1..];
        }

        if (!rest.StartsWith('?'))
        {
            return false;
        }

        string? d = null;
        string? s = null;
        foreach (var pair in rest[1..].Split('&'))
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                return false;
            }

            var name = pair[..equals];
            var value = pair[(equals + 1)..];
            if (name == "d" && d is null)
            {
                d = value;
            }
            else if (name == "s" && s is null)
            {
                s = value;
            }
            else
            {
                return false;
            }
        }

        if (d is null || s is null || d.Length == 0 || !IsBase64Url(d)
            || s.Length != SignatureLength || !IsBase64Url(s))
        {
            return false;
        }

        data = d;
        signature = s;
        return true;
    }

    private static bool SignatureMatches(string data, string signature, byte[] key)
    {
        Span<byte> given = stackalloc byte[32];
        if (!Base64Url.TryDecodeFromChars(signature, given, out var written) || written != given.Length)
        {
            return false;
        }

        var expected = HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(data));
        return CryptographicOperations.FixedTimeEquals(expected, given);
    }

    internal static bool IsBase64Url(string value)
    {
        foreach (var c in value)
        {
            if (c is not ((>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static string? CheckShape(HandoverDocument document)
    {
        if (document.Bills is null || document.Bills.Count == 0)
        {
            return Unreadable;
        }

        if (document.Bills.Count > MaxBills)
        {
            return $"This hand-over has more than {MaxBills} bills.";
        }

        var notSent = document.NotSent ?? [];
        if (notSent.Count > MaxNotSent)
        {
            return Unreadable;
        }

        var saveIds = new HashSet<Guid>();
        foreach (var bill in document.Bills)
        {
            if (bill is null || bill.SaveId == Guid.Empty || !saveIds.Add(bill.SaveId))
            {
                return Unreadable;
            }

            if (string.IsNullOrWhiteSpace(bill.PartyName)
                || bill.PartyName.Length > MaxPartyNameLength
                || bill.PartyPhone?.Length > 64
                || bill.PartyAddress?.Length > 1_000
                || bill.Payment is not (PaymentMode.Cash or PaymentMode.CreditDebit))
            {
                return Unreadable;
            }

            if (bill.Lines is null || bill.Lines.Count == 0)
            {
                return Unreadable;
            }

            if (bill.Lines.Count > MaxLines)
            {
                return $"A bill in this hand-over has more than {MaxLines} lines.";
            }

            foreach (var line in bill.Lines)
            {
                if (line is null || !LineIsWellFormed(line))
                {
                    return Unreadable;
                }
            }
        }

        foreach (var id in notSent)
        {
            if (id == Guid.Empty || saveIds.Contains(id))
            {
                return Unreadable;
            }
        }

        return null;
    }

    private static bool LineIsWellFormed(HandoverLine line)
        => !string.IsNullOrWhiteSpace(line.ItemName)
           && line.ItemName.Length <= 256
           && (line.Karat is null || (line.Karat.Trim().Length > 0 && line.Karat.Length <= 32))
           && line.Pieces is >= 0 and <= 100_000
           && IsWeight(line.GrossWeight)
           && IsWeight(line.LessWeight)
           && IsWeight(line.NetWeight)
           && line.WastagePercent is >= -100m and <= 1_000m
           && (line.Rate is null || line.Rate is >= 0m and <= MaxMoney)
           && IsMoney(line.LabourAmount)
           && IsMoney(line.StoneValue)
           && IsMoney(line.HallmarkAmount)
           && IsMoney(line.DiscountAmount)
           && IsMoney(line.LineTotal)
           // Kept exactly, so it must be whole paise.
           && decimal.Round(line.LineTotal, 2) == line.LineTotal;

    private static bool IsWeight(decimal value) => value is >= 0m and <= MaxWeight && decimal.Round(value, 3) == value;

    private static bool IsMoney(decimal value) => value is >= 0m and <= MaxMoney;
}
