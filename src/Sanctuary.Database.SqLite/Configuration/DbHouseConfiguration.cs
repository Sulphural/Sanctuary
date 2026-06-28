using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Sanctuary.Database.Entities;

namespace Sanctuary.Database.Sqlite.Configuration;

public sealed class DbHouseConfiguration : IEntityTypeConfiguration<DbHouse>
{
    public void Configure(EntityTypeBuilder<DbHouse> builder)
    {
        builder.HasKey(h => h.Id);
        builder.Property(h => h.Id).IsRequired().ValueGeneratedOnAdd();

        builder.HasIndex(h => h.OwnerId);
        builder.Property(h => h.OwnerId).IsRequired();

        builder.Property(h => h.HouseDefinitionId).IsRequired();

        builder.Property(h => h.NameId).IsRequired();
        builder.Property(h => h.CustomName).IsRequired(false).HasMaxLength(128);

        builder.Property(h => h.IsLocked).IsRequired().HasDefaultValue(false);
        builder.Property(h => h.IsMembersOnly).IsRequired().HasDefaultValue(false);
        builder.Property(h => h.IsFloraAllowed).IsRequired().HasDefaultValue(true);
        builder.Property(h => h.PetAutospawn).IsRequired().HasDefaultValue(false);

        builder.Property(h => h.MaxFixtureCount).IsRequired().HasDefaultValue(100);
        builder.Property(h => h.MaxLandmarkCount).IsRequired().HasDefaultValue(10);

        builder.Property(h => h.IconId).IsRequired().HasDefaultValue(0);

        builder.Property(h => h.Description).IsRequired(false).HasMaxLength(512);
        builder.Property(h => h.KeywordList).IsRequired(false).HasMaxLength(256);

        builder.Property(h => h.Rating).IsRequired().HasDefaultValue(0.0f);
        builder.Property(h => h.Votes).IsRequired().HasDefaultValue(0);

        builder.Property(h => h.Created).IsRequired().HasDefaultValueSql("DATE()");
        builder.Property(h => h.LastVisited).IsRequired().HasDefaultValueSql("DATE()");

        builder.HasOne(h => h.Owner)
            .WithMany()
            .HasForeignKey(h => h.OwnerId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(h => h.Fixtures)
            .WithOne(f => f.House)
            .HasForeignKey(f => f.HouseId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(h => h.Permissions)
            .WithOne(p => p.House)
            .HasForeignKey(p => p.HouseId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
