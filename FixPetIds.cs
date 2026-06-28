using System;
using System.IO;
using Microsoft.Data.Sqlite;

class Program
{
    static void Main()
    {
        var dbPath = @"C:\Users\nadim\Desktop\NEWSanctServer\Sanctuary\src\bin\Release\sanctuary.db";
        var connectionString = $"Data Source={dbPath}";

        using (var connection = new SqliteConnection(connectionString))
        {
            connection.Open();

            // Delete existing test pets for character 2
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "DELETE FROM Pets WHERE CharacterId = 2";
                command.ExecuteNonQuery();
                Console.WriteLine("Deleted old pets.");
            }

            // Add new pets with correct Definition IDs
            var pets = new[]
            {
                (Name: "Sparkles", Definition: 1088, Tint: 232), // Wolf Pet
                (Name: "Flame", Definition: 4243, Tint: 227)      // Lava Tiger Pet
            };

            foreach (var pet in pets)
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "INSERT INTO Pets (CharacterId, Name, Tint, Definition, Created) VALUES (@CharacterId, @Name, @Tint, @Definition, @Created)";
                    command.Parameters.AddWithValue("@CharacterId", 2);
                    command.Parameters.AddWithValue("@Name", pet.Name);
                    command.Parameters.AddWithValue("@Tint", pet.Tint);
                    command.Parameters.AddWithValue("@Definition", pet.Definition);
                    command.Parameters.AddWithValue("@Created", DateTime.UtcNow);
                    command.ExecuteNonQuery();
                    Console.WriteLine($"Added pet: {pet.Name} (Definition: {pet.Definition})");
                }
            }

            // Verify
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT Id, Definition, Name, Tint FROM Pets WHERE CharacterId = 2";
                using (var reader = command.ExecuteReader())
                {
                    Console.WriteLine("\nUpdated pets:");
                    while (reader.Read())
                    {
                        Console.WriteLine($"  ID: {reader["Id"]}, Definition: {reader["Definition"]}, Name: {reader["Name"]}, Tint: {reader["Tint"]}");
                    }
                }
            }
        }

        Console.WriteLine("\nDone!");
    }
}
