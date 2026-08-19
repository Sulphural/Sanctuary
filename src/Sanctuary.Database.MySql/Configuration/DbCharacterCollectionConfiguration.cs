using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Sanctuary.Database.Entities;

namespace Sanctuary.Database.MySql.Configuration;

public sealed class DbCharacterCollectionConfiguration : IEntityTypeConfiguration<DbCharacterCollection>
{
    public void Configure(EntityTypeBuilder<DbCharacterCollection> builder)
    {
        builder.HasKey(c => new { c.CollectionId, c.CharacterId });
        builder.Property(c => c.CollectionId).IsRequired().ValueGeneratedNever();
    }
}
