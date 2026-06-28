using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Sanctuary.Database.Entities;

namespace Sanctuary.Database.Sqlite.Configuration;

public sealed class DbHousePermissionConfiguration : IEntityTypeConfiguration<DbHousePermission>
{
    public void Configure(EntityTypeBuilder<DbHousePermission> builder)
    {
        builder.HasKey(p => new { p.HouseId, p.CharacterId });

        builder.Property(p => p.HouseId).IsRequired();
        builder.Property(p => p.CharacterId).IsRequired();
        builder.Property(p => p.PermissionLevel).IsRequired().HasDefaultValue(0);

        builder.Property(p => p.Created).IsRequired().HasDefaultValueSql("DATE()");

        builder.HasOne(p => p.House)
            .WithMany(h => h.Permissions)
            .HasForeignKey(p => p.HouseId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(p => p.Character)
            .WithMany()
            .HasForeignKey(p => p.CharacterId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(p => p.CharacterId);
    }
}
