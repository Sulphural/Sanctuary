using Microsoft.Data.Sqlite;

Console.WriteLine("Starting database migration...");

var connectionString = args.Length > 0 ? args[0] : "Data Source=../bin/Release/sanctuary.db";
Console.WriteLine($"Using database: {connectionString}");

using var connection = new SqliteConnection(connectionString);
connection.Open();

Console.WriteLine("Adding missing columns to Mounts table...");
try
{
    using (var cmd = connection.CreateCommand())
    {
        cmd.CommandText = "ALTER TABLE Mounts ADD COLUMN Tint INTEGER NOT NULL DEFAULT 0";
        cmd.ExecuteNonQuery();
        Console.WriteLine("  - Added Tint column");
    }
}
catch (SqliteException ex) when (ex.Message.Contains("duplicate column"))
{
    Console.WriteLine("  - Tint column already exists");
}

try
{
    using (var cmd = connection.CreateCommand())
    {
        cmd.CommandText = "ALTER TABLE Mounts ADD COLUMN Definition INTEGER NOT NULL DEFAULT 0";
        cmd.ExecuteNonQuery();
        Console.WriteLine("  - Added Definition column");
    }
}
catch (SqliteException ex) when (ex.Message.Contains("duplicate column"))
{
    Console.WriteLine("  - Definition column already exists");
}

Console.WriteLine("Creating Pets table...");
using (var cmd = connection.CreateCommand())
{
    cmd.CommandText = @"
        CREATE TABLE IF NOT EXISTS Pets (
            Id INTEGER NOT NULL,
            CharacterId INTEGER NOT NULL,
            Name TEXT NOT NULL,
            Tint INTEGER NOT NULL,
            Definition INTEGER NOT NULL,
            Created TEXT NOT NULL DEFAULT (datetime('now')),
            CONSTRAINT PK_Pets PRIMARY KEY (Id, CharacterId),
            CONSTRAINT FK_Pets_Characters_CharacterId FOREIGN KEY (CharacterId) REFERENCES Characters (Id) ON DELETE CASCADE
        )";
    cmd.ExecuteNonQuery();
    Console.WriteLine("  - Pets table created");
}

Console.WriteLine("Creating indexes...");
using (var cmd = connection.CreateCommand())
{
    cmd.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS IX_Mounts_Tint_Definition_CharacterId ON Mounts (Tint, Definition, CharacterId)";
    cmd.ExecuteNonQuery();
    Console.WriteLine("  - Created Mounts index");
}

using (var cmd = connection.CreateCommand())
{
    cmd.CommandText = "CREATE INDEX IF NOT EXISTS IX_Pets_CharacterId ON Pets (CharacterId)";
    cmd.ExecuteNonQuery();
    Console.WriteLine("  - Created Pets CharacterId index");
}

using (var cmd = connection.CreateCommand())
{
    cmd.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS IX_Pets_Tint_Definition_CharacterId ON Pets (Tint, Definition, CharacterId)";
    cmd.ExecuteNonQuery();
    Console.WriteLine("  - Created Pets unique index");
}

Console.WriteLine("\nMigration completed successfully!");

// Remove test pets if they exist
Console.WriteLine("\nRemoving test pets from character 1...");
using (var deleteCmd = connection.CreateCommand())
{
    deleteCmd.CommandText = "DELETE FROM Pets WHERE CharacterId = 1";
    int deleted = deleteCmd.ExecuteNonQuery();
    if (deleted > 0)
        Console.WriteLine($"  - Removed {deleted} test pets");
    else
        Console.WriteLine("  - No pets to remove");
}

Console.WriteLine("\nPress any key to exit...");
Console.ReadKey();
