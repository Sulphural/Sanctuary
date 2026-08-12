using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Sanctuary.Database.Entities;

namespace Sanctuary.Database.Sqlite.Configuration;

public sealed class DbCharacterDailyWheelConfiguration : IEntityTypeConfiguration<DbCharacterDailyWheel>
{
    public void Configure(EntityTypeBuilder<DbCharacterDailyWheel> builder)
    {
        builder.HasKey(w => new { w.WheelId, w.CharacterId });
        builder.Property(w => w.WheelId).IsRequired().ValueGeneratedNever();
    }
}
