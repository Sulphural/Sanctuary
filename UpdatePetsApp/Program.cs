using Microsoft.Data.Sqlite;
using System;
using System.IO;

// Configuration
var dbPath = @"c:\Users\nadim\Desktop\NEWSanctServer\Sanctuary\src\bin\Release\sanctuary.db";
var characterId = 2; // Change this to your character ID if different

Console.WriteLine($"Adding test pets to character ID {characterId}...");
Console.WriteLine($"Database: {dbPath}");

if (!File.Exists(dbPath))
{
    Console.WriteLine($"ERROR: Database not found at {dbPath}");
    return 1;
}

try
{
    using var connection = new SqliteConnection($"Data Source={dbPath}");
    connection.Open();

    // First, get max pet ID so we can create new ones if needed
    using var maxIdCmd = connection.CreateCommand();
    maxIdCmd.CommandText = "SELECT MAX(Id) FROM Pets";
    var maxIdObj = maxIdCmd.ExecuteScalar();
    var maxId = (maxIdObj is DBNull) ? 0 : ((long)maxIdObj);

    // Check how many pets exist for this character
    using var countCmd = connection.CreateCommand();
    countCmd.CommandText = "SELECT COUNT(*) FROM Pets WHERE CharacterId = @CharacterId";
    countCmd.Parameters.AddWithValue("@CharacterId", characterId);
    var petCount = (long)countCmd.ExecuteScalar()!;

    if (petCount == 0)
    {
        Console.WriteLine("No existing pets found. Creating new ones...");
        var pets = new[]
        {
            (Name: "Sparkles", Definition: 1088, Tint: 232),    // Wolf Pet
            (Name: "Flame", Definition: 4243, Tint: 227)         // Lava Tiger Pet
        };

        foreach (var pet in pets)
        {
            maxId++;
            using var insertCmd = connection.CreateCommand();
            insertCmd.CommandText = @"
                INSERT INTO Pets (Id, CharacterId, Name, Tint, Definition, Created)
                VALUES (@Id, @CharacterId, @Name, @Tint, @Definition, datetime('now'))";

            insertCmd.Parameters.AddWithValue("@Id", maxId);
            insertCmd.Parameters.AddWithValue("@CharacterId", characterId);
            insertCmd.Parameters.AddWithValue("@Name", pet.Name);
            insertCmd.Parameters.AddWithValue("@Tint", pet.Tint);
            insertCmd.Parameters.AddWithValue("@Definition", pet.Definition);

            insertCmd.ExecuteNonQuery();
            Console.WriteLine($"Created pet: {pet.Name} (Definition: {pet.Definition})");
        }
    }
    else
    {
        Console.WriteLine($"Found {petCount} existing pets. Updating definitions...");
        // Update existing pets - set their Definition to the correct IDs
        using var updateCmd = connection.CreateCommand();
        updateCmd.CommandText = "UPDATE Pets SET Definition = CASE WHEN Name = 'Sparkles' THEN 1088 ELSE 4243 END WHERE CharacterId = @CharacterId";
        updateCmd.Parameters.AddWithValue("@CharacterId", characterId);
        var updated = updateCmd.ExecuteNonQuery();
        Console.WriteLine($"Updated {updated} pets");

        // Also update the Tint values
        using var tintCmd = connection.CreateCommand();
        tintCmd.CommandText = "UPDATE Pets SET Tint = CASE WHEN Name = 'Sparkles' THEN 232 ELSE 227 END WHERE CharacterId = @CharacterId";
        tintCmd.Parameters.AddWithValue("@CharacterId", characterId);
        var tintUpdated = tintCmd.ExecuteNonQuery();
        Console.WriteLine($"Updated tints for {tintUpdated} pets");
    }

    // Verify
    using var verifyCmd = connection.CreateCommand();
    verifyCmd.CommandText = "SELECT Id, Definition, Name, Tint FROM Pets WHERE CharacterId = @CharacterId";
    verifyCmd.Parameters.AddWithValue("@CharacterId", characterId);
    using var reader = verifyCmd.ExecuteReader();
    Console.WriteLine("\nPets in database:");
    var hasRows = false;
    while (reader.Read())
    {
        hasRows = true;
        Console.WriteLine($"  DbId: {reader["Id"]}, Definition: {reader["Definition"]}, Name: {reader["Name"]}, Tint: {reader["Tint"]}");
    }

    if (!hasRows)
        Console.WriteLine("  No pets found!");

    Console.WriteLine("\nDone! Restart your server and log in to test.");
    Console.WriteLine("Use: !petspawn 1 or !petspawn 2");

    return 0;
}
catch (Exception ex)
{
    Console.WriteLine($"ERROR: {ex.Message}");
    Console.WriteLine(ex.StackTrace);
    return 1;
}
