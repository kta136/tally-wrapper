using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ShowroomBilling.Application.Admin;
using ShowroomBilling.Contracts.Admin;
using ShowroomBilling.Infrastructure.Persistence;
using ShowroomBilling.Infrastructure.Persistence.Entities;

namespace ShowroomBilling.Infrastructure.Admin;

public sealed class AdminAuthService(
    ShowroomBillingDbContext dbContext,
    IAdminUnlockThrottle unlockThrottle,
    ILogger<AdminAuthService> logger) : IAdminAuthService
{
    private const string DefaultShowroomCode = "default";
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int PbkdfIterations = 120_000;
    private const int TokenBytes = 32;
    private const int MinPasscodeLength = 6;
    private static readonly TimeSpan SessionTtl = TimeSpan.FromMinutes(30);
    private const string DefaultActorLabel = "admin";

    public async Task<AdminPasscodeStatusResponse> GetPasscodeStatusAsync(CancellationToken cancellationToken = default)
    {
        var showroomId = ResolveShowroomId(DefaultShowroomCode);
        var exists = await dbContext.AdminPasscodes.AnyAsync(x => x.ShowroomId == showroomId, cancellationToken);
        return new AdminPasscodeStatusResponse(exists);
    }

    public async Task SetPasscodeAsync(AdminSetPasscodeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.NewPasscode) || request.NewPasscode.Length < MinPasscodeLength)
        {
            throw new ArgumentException(
                $"New passcode must be at least {MinPasscodeLength} characters.",
                nameof(request));
        }

        var now = DateTimeOffset.UtcNow;
        var showroomId = ResolveShowroomId(DefaultShowroomCode);
        var existing = await dbContext.AdminPasscodes
            .FirstOrDefaultAsync(x => x.ShowroomId == showroomId, cancellationToken);

        if (existing is not null)
        {
            if (string.IsNullOrEmpty(request.CurrentPasscode) || !VerifyPasscode(request.CurrentPasscode, existing))
            {
                throw new AdminPasscodeInvalidException();
            }

            (existing.PasscodeSalt, existing.PasscodeHash, existing.Iterations) = HashPasscode(request.NewPasscode);
            existing.UpdatedAtUtc = now;
        }
        else
        {
            var (salt, hash, iter) = HashPasscode(request.NewPasscode);
            dbContext.AdminPasscodes.Add(new AdminPasscodeEntity
            {
                Id = Guid.CreateVersion7(),
                ShowroomId = showroomId,
                PasscodeSalt = salt,
                PasscodeHash = hash,
                Iterations = iter,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
        }

        WriteAudit("admin.passcode.set", now, null);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<AdminUnlockResponse> UnlockAsync(AdminUnlockRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var showroomId = ResolveShowroomId(DefaultShowroomCode);

        // Throttle BEFORE we touch the DB so that a hammering caller can't
        // even cause repeated PBKDF2 work, and a 4xx is returned cheaply.
        var cooldown = unlockThrottle.GetCooldown(showroomId);
        if (cooldown > TimeSpan.Zero)
        {
            logger.LogWarning(
                "Admin unlock throttled for showroom {ShowroomId}: {SecondsRemaining}s remaining.",
                showroomId, (int)Math.Ceiling(cooldown.TotalSeconds));
            throw new AdminUnlockThrottledException(cooldown);
        }

        var passcode = await dbContext.AdminPasscodes
            .FirstOrDefaultAsync(x => x.ShowroomId == showroomId, cancellationToken)
            ?? throw new AdminPasscodeNotConfiguredException();

        if (string.IsNullOrEmpty(request.Passcode) || !VerifyPasscode(request.Passcode, passcode))
        {
            var nextCooldown = unlockThrottle.RegisterFailure(showroomId);
            var failedAt = DateTimeOffset.UtcNow;
            logger.LogWarning(
                "Admin unlock failed for showroom {ShowroomId}; next allowed in {SecondsRemaining}s.",
                showroomId, (int)Math.Ceiling(nextCooldown.TotalSeconds));
            WriteAudit("admin.unlock.failed", failedAt, actorLabel: null);
            await dbContext.SaveChangesAsync(cancellationToken);
            throw new AdminPasscodeInvalidException();
        }

        unlockThrottle.Reset(showroomId);

        var now = DateTimeOffset.UtcNow;
        var rawToken = GenerateRawToken();
        var tokenHash = HashToken(rawToken);
        var actorLabel = string.IsNullOrWhiteSpace(request.ActorLabel) ? DefaultActorLabel : request.ActorLabel.Trim();

        var session = new AdminSessionEntity
        {
            Id = Guid.CreateVersion7(),
            ShowroomId = showroomId,
            TokenHash = tokenHash,
            ActorLabel = actorLabel,
            IssuedAtUtc = now,
            ExpiresAtUtc = now.Add(SessionTtl)
        };
        dbContext.AdminSessions.Add(session);
        WriteAudit("admin.unlock", now, actorLabel);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new AdminUnlockResponse(rawToken, session.IssuedAtUtc, session.ExpiresAtUtc, actorLabel);
    }

    public async Task<AdminSessionInfoResponse?> ValidateTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }
        var hash = HashToken(token);
        var now = DateTimeOffset.UtcNow;
        var session = await dbContext.AdminSessions
            .FirstOrDefaultAsync(
                x => x.TokenHash == hash && x.RevokedAtUtc == null && x.ExpiresAtUtc > now,
                cancellationToken);
        if (session is null)
        {
            return null;
        }
        return new AdminSessionInfoResponse(session.Id, session.ActorLabel, session.IssuedAtUtc, session.ExpiresAtUtc);
    }

    public async Task LogoutAsync(AdminLogoutRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Token))
        {
            return;
        }
        var hash = HashToken(request.Token);
        var session = await dbContext.AdminSessions
            .FirstOrDefaultAsync(x => x.TokenHash == hash && x.RevokedAtUtc == null, cancellationToken);
        if (session is null)
        {
            return;
        }
        var now = DateTimeOffset.UtcNow;
        session.RevokedAtUtc = now;
        session.RevokedReason = "logout";
        WriteAudit("admin.logout", now, session.ActorLabel);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static (string Salt, string Hash, int Iterations) HashPasscode(string passcode)
    {
        var saltBytes = RandomNumberGenerator.GetBytes(SaltBytes);
        var hashBytes = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passcode),
            saltBytes,
            PbkdfIterations,
            HashAlgorithmName.SHA256,
            HashBytes);
        return (Convert.ToBase64String(saltBytes), Convert.ToBase64String(hashBytes), PbkdfIterations);
    }

    private static bool VerifyPasscode(string passcode, AdminPasscodeEntity entity)
    {
        var saltBytes = Convert.FromBase64String(entity.PasscodeSalt);
        var attempt = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passcode),
            saltBytes,
            entity.Iterations,
            HashAlgorithmName.SHA256,
            HashBytes);
        var stored = Convert.FromBase64String(entity.PasscodeHash);
        return CryptographicOperations.FixedTimeEquals(attempt, stored);
    }

    private static string GenerateRawToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(TokenBytes);
        return Convert.ToBase64String(bytes);
    }

    private static string HashToken(string token)
    {
        var bytes = Encoding.UTF8.GetBytes(token);
        var hash = SHA256.HashData(bytes);
        return Convert.ToBase64String(hash);
    }

    private void WriteAudit(string eventType, DateTimeOffset at, string? actorLabel)
    {
        dbContext.AuditEvents.Add(new AuditEventEntity
        {
            Id = Guid.CreateVersion7(),
            EntityType = "admin",
            EntityId = ResolveShowroomId(DefaultShowroomCode).ToString(),
            EventType = eventType,
            ActorType = "admin",
            ActorId = actorLabel,
            PayloadJson = "{}",
            CreatedAtUtc = at
        });
    }

    private static Guid ResolveShowroomId(string showroomCode)
    {
        var bytes = Encoding.UTF8.GetBytes(showroomCode.Trim().ToLowerInvariant());
        var hash = MD5.HashData(bytes);
        return new Guid(hash);
    }
}
