using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Contracts.PrintAssets;
using ShowroomBilling.Infrastructure.Persistence;
using ShowroomBilling.Infrastructure.PrintAssets;

namespace ShowroomBilling.Tests;

public sealed class PrintAssetServiceTests
{
    [Fact]
    public async Task UploadAsync_PersistsBytes_AndAssignsId()
    {
        await using var db = CreateDbContext();
        var service = new PrintAssetService(db);
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 };

        var response = await service.UploadAsync(new PrintAssetUploadRequest(
            PrintAssetKinds.Logo,
            "logo.png",
            "image/png",
            Convert.ToBase64String(bytes)));

        Assert.NotEqual(Guid.Empty, response.Id);
        Assert.Equal("logo", response.AssetKind);
        Assert.Equal(bytes.Length, response.ByteLength);
    }

    [Fact]
    public async Task GetAsync_ReturnsOriginalBytes()
    {
        await using var db = CreateDbContext();
        var service = new PrintAssetService(db);
        var bytes = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 };
        var uploaded = await service.UploadAsync(new PrintAssetUploadRequest(
            PrintAssetKinds.Signature,
            "sign.png",
            "image/png",
            Convert.ToBase64String(bytes)));

        var result = await service.GetAsync(uploaded.Id);

        Assert.NotNull(result);
        Assert.Equal(bytes, result!.Value.Bytes);
        Assert.Equal("signature", result.Value.Metadata.AssetKind);
    }

    [Fact]
    public async Task UploadAsync_RejectsUnknownKind()
    {
        await using var db = CreateDbContext();
        var service = new PrintAssetService(db);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.UploadAsync(new PrintAssetUploadRequest(
                "poster",
                "x.png",
                "image/png",
                Convert.ToBase64String(new byte[] { 1, 2 }))));
    }

    [Fact]
    public async Task DeleteAsync_RemovesAsset()
    {
        await using var db = CreateDbContext();
        var service = new PrintAssetService(db);
        var uploaded = await service.UploadAsync(new PrintAssetUploadRequest(
            PrintAssetKinds.Logo,
            "logo.png",
            "image/png",
            Convert.ToBase64String(new byte[] { 1, 2, 3 })));

        var deleted = await service.DeleteAsync(uploaded.Id);

        Assert.True(deleted);
        var list = await service.ListAsync();
        Assert.Empty(list.Assets);
    }

    [Fact]
    public async Task ListAsync_ReturnsAllUploaded()
    {
        await using var db = CreateDbContext();
        var service = new PrintAssetService(db);
        await service.UploadAsync(new PrintAssetUploadRequest(
            PrintAssetKinds.Logo, "a.png", "image/png", Convert.ToBase64String(new byte[] { 1 })));
        await service.UploadAsync(new PrintAssetUploadRequest(
            PrintAssetKinds.Signature, "b.png", "image/png", Convert.ToBase64String(new byte[] { 2 })));

        var list = await service.ListAsync();

        Assert.Equal(2, list.Assets.Count);
    }

    private static ShowroomBillingDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ShowroomBillingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ShowroomBillingDbContext(options);
    }
}
