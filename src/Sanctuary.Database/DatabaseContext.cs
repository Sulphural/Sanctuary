using System;
using System.Linq;
using System.Reflection;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

using Sanctuary.Database.Entities;

namespace Sanctuary.Database;

public sealed class DatabaseContext : DbContext
{
    public DbSet<DbUser> Users => Set<DbUser>();
    public DbSet<DbItem> Items => Set<DbItem>();
    public DbSet<DbTitle> Titles => Set<DbTitle>();
    public DbSet<DbMount> Mounts => Set<DbMount>();
    public DbSet<DbPet> Pets => Set<DbPet>();
    public DbSet<DbFriend> Friends => Set<DbFriend>();
    public DbSet<DbIgnore> Ignores => Set<DbIgnore>();
    public DbSet<DbProfile> Profiles => Set<DbProfile>();
    public DbSet<DbCharacter> Characters => Set<DbCharacter>();
    public DbSet<DbHouse> Houses => Set<DbHouse>();
    public DbSet<DbHouseFixture> HouseFixtures => Set<DbHouseFixture>();
    public DbSet<DbHousePermission> HousePermissions => Set<DbHousePermission>();

    public DatabaseContext()
    {
        if (Database.IsSqlite())
        {
            // Enable foreign keys for SQLite
            Database.OpenConnection();
            Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON;");
        }
    }

    public DatabaseContext(DbContextOptions options) : base(options)
    {
        if (Database.IsSqlite())
        {
            // Enable foreign keys for SQLite
            Database.OpenConnection();
            Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON;");
        }
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
#if DEBUG
        // optionsBuilder.EnableDetailedErrors();
        // optionsBuilder.EnableSensitiveDataLogging();
#endif
        // Temporarily suppress pending model changes warning
        optionsBuilder.ConfigureWarnings(warnings =>
            warnings.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var assembly = LoadProviderAssembly();

        ArgumentNullException.ThrowIfNull(assembly);

        modelBuilder.ApplyConfigurationsFromAssembly(assembly);

        base.OnModelCreating(modelBuilder);
    }

    private Assembly? LoadProviderAssembly()
    {
        var prefix = $"{typeof(DatabaseFactory).Namespace}.";

        return AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a =>
            {
                var name = a.GetName().Name;
                return name is not null
                       && name.StartsWith(prefix, StringComparison.Ordinal)
                       && (name.EndsWith(".MySql", StringComparison.Ordinal) || name.EndsWith(".SqLite", StringComparison.Ordinal));
            });
    }
}