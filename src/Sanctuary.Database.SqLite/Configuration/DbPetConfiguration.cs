using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Sanctuary.Database.Entities;

namespace Sanctuary.Database.Sqlite.Configuration;

public sealed class DbPetConfiguration : IEntityTypeConfiguration<DbPet>
{
    public void Configure(EntityTypeBuilder<DbPet> builder)
    {
        builder.HasKey(p => new { p.Id, p.CharacterId });
        builder.Property(p => p.Id).IsRequired().ValueGeneratedNever();
        builder.HasIndex(p => new { p.Tint, p.Definition, p.CharacterId }).IsUnique();

        builder.Property(p => p.Name).IsRequired().HasMaxLength(32);
        builder.Property(p => p.Tint).IsRequired();
        builder.Property(p => p.Definition).IsRequired();

        builder.Property(p => p.Created).IsRequired().HasDefaultValueSql("datetime('now')");
    }
}
