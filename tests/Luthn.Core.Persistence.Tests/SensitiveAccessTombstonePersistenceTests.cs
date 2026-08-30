using Luthn.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Luthn.Core.Persistence.Tests;

public sealed class SensitiveAccessTombstonePersistenceTests
{
    [Fact]
    public async Task TombstonePersistsOnlyContentFreeExpiryMetadata()
    {
        await using var db = CreateDatabase();
        var expiredAt = DateTimeOffset.Parse("2026-08-12T00:00:00Z");
        db.SensitiveAccessTombstones.Add(new SensitiveAccessTombstoneRecord
        {
            Id = "access-expired",
            Status = SensitiveAccessRequestStatus.Expired,
            ExpiredAt = expiredAt,
            CleanedAt = expiredAt.AddMinutes(1),
            WorkspaceId = "default",
            OwnerUserId = "local-owner"
        });

        await db.SaveChangesAsync();

        var tombstone = await db.SensitiveAccessTombstones.SingleAsync();
        Assert.Equal(SensitiveAccessRequestStatus.Expired, tombstone.Status);
        Assert.Equal(
            ["Id", "Status", "ExpiredAt", "CleanedAt", "WorkspaceId", "OwnerUserId"],
            typeof(SensitiveAccessTombstoneRecord)
                .GetProperties()
                .Select(property => property.Name)
                .ToArray());
    }

    [Fact]
    public async Task TombstoneRejectsNonExpiredStatusAndInvalidTimeline()
    {
        await using var db = CreateDatabase();
        db.SensitiveAccessTombstones.Add(new SensitiveAccessTombstoneRecord
        {
            Id = "access-invalid",
            Status = SensitiveAccessRequestStatus.Approved,
            ExpiredAt = DateTimeOffset.Parse("2026-08-12T00:01:00Z"),
            CleanedAt = DateTimeOffset.Parse("2026-08-12T00:00:00Z"),
            WorkspaceId = "default",
            OwnerUserId = "local-owner"
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    private static LuthnDbContext CreateDatabase() =>
        new(new DbContextOptionsBuilder<LuthnDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);
}
