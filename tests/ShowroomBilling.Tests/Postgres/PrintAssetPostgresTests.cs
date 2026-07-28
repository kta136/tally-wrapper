using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Contracts.PrintAssets;
using ShowroomBilling.Infrastructure.Persistence.Entities;
using ShowroomBilling.Infrastructure.PrintAssets;

namespace ShowroomBilling.Tests.Postgres;

[Collection(PostgresCollection.Name)]
public sealed class PrintAssetPostgresTests(PostgresFixture fixture)
{
    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task Asset_kind_constraint_accepts_watermark_and_rejects_unknown_values()
    {
        await using var database = await fixture.CreateDatabaseAsync();

        await using (var acceptedContext = database.CreateContext())
        {
            var service = new PrintAssetService(acceptedContext);
            var response = await service.UploadAsync(new PrintAssetUploadRequest(
                PrintAssetKinds.Watermark,
                "watermark.png",
                "image/png",
                Convert.ToBase64String(
                    [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01])));

            Assert.Equal(PrintAssetKinds.Watermark, response.AssetKind);
        }

        await using var rejectedContext = database.CreateContext();
        rejectedContext.PrintAssets.Add(new PrintAssetEntity
        {
            Id = Guid.NewGuid(),
            ShowroomId = Guid.NewGuid(),
            AssetKind = "poster",
            FileName = "poster.png",
            ContentType = "image/png",
            ByteLength = 8,
            Bytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A],
            CreatedAtUtc = DateTimeOffset.UtcNow
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => rejectedContext.SaveChangesAsync());
    }
}
