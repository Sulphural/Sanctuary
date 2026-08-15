using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Sanctuary.Database.Entities;

namespace Sanctuary.Database.Sqlite.Configuration;

public sealed class DbUserConfiguration : IEntityTypeConfiguration<DbUser>
{
    public void Configure(EntityTypeBuilder<DbUser> builder)
    {
        builder.HasKey(u => u.Id);
        builder.Property(u => u.Id).IsRequired().ValueGeneratedOnAdd();

        builder.HasIndex(u => u.Username).IsUnique();
        builder.Property(u => u.Username).IsRequired().HasMaxLength(254);
        builder.Property(u => u.Password).IsRequired().HasMaxLength(254);

        builder.Property(u => u.Session).IsRequired(false).HasMaxLength(32);
        builder.Property(u => u.SessionCreated).IsRequired(false);

        builder.Property(u => u.MaxCharacters).IsRequired().HasDefaultValue(10);

        // No HasDefaultValue here on purpose: EF treats a configured default as the property's
        // SENTINEL, so IsMember = true was read as "not set", dropped from the INSERT, and the
        // column's own default (0) won — every new account landed non-member. Leaving the default
        // off makes the sentinel the CLR default (false), so an explicit true is always written.
        builder.Property(u => u.IsMember).IsRequired();
        builder.Property(u => u.IsAdmin).IsRequired().HasDefaultValue(false);
        builder.Property(u => u.IsMod).IsRequired().HasDefaultValue(false);
        builder.Property(u => u.LockedUntil).IsRequired(false);
        builder.Property(u => u.MutedUntil).IsRequired(false);

        builder.Property(u => u.Created).IsRequired().HasDefaultValueSql("DATE()");
        builder.Property(u => u.LastLogin).IsRequired(false);

        builder.HasMany(u => u.Characters)
            .WithOne(c => c.User)
            .HasForeignKey(c => c.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}