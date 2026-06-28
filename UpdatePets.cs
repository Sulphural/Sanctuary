using Microsoft.Data.Sqlite;
using System;

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

    // Delete existing test pets
    using var deleteCmd = connection.CreateCommand();
    deleteCmd.CommandText = "DELETE FROM Pets WHERE CharacterId = @CharacterId";
    deleteCmd.Parameters.AddWithValue("@CharacterId", characterId);
    var deleted = deleteCmd.ExecuteNonQuery();
    Console.WriteLine($"Deleted {deleted} existing pets");

    // Add test pets
    var pets = new[]
    {
        (Name: "Sparkles", Definition: 1088, Tint: 232),    // Wolf Pet
        (Name: "Flame", Definition: 4243, Tint: 227)         // Lava Tiger Pet
    };

    foreach (var pet in pets)
    {
        using var insertCmd = connection.CreateCommand();
        insertCmd.CommandText = @"
            INSERT INTO Pets (CharacterId, Name, Tint, Definition, Created)
            VALUES (@CharacterId, @Name, @Tint, @Definition, datetime('now'))";

        insertCmd.Parameters.AddWithValue("@CharacterId", characterId);
        insertCmd.Parameters.AddWithValue("@Name", pet.Name);
        insertCmd.Parameters.AddWithValue("@Tint", pet.Tint);
        insertCmd.Parameters.AddWithValue("@Definition", pet.Definition);

        insertCmd.ExecuteNonQuery();
        Console.WriteLine($"Added pet: {pet.Name} (Definition: {pet.Definition})");
    }

    // Verify
    using var verifyCmd = connection.CreateCommand();
    verifyCmd.CommandText = "SELECT Id, Definition, Name, Tint FROM Pets WHERE CharacterId = @CharacterId";
    verifyCmd.Parameters.AddWithValue("@CharacterId", characterId);
    using var reader = verifyCmd.ExecuteReader();
    Console.WriteLine("\nUpdated pets in database:");
    while (reader.Read())
    {
        Console.WriteLine($"  DbId: {reader["Id"]}, Definition: {reader["Definition"]}, Name: {reader["Name"]}, Tint: {reader["Tint"]}");
    }

    Console.WriteLine("\nSuccess! Restart your server and log in to test.");
    Console.WriteLine("Use: !petspawn 1 or !petspawn 2");

    return 0;
}
catch (Exception ex)
{
    Console.WriteLine($"ERROR: {ex.Message}");
    Console.WriteLine(ex.StackTrace);
    return 1;
}
