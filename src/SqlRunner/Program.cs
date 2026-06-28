using Microsoft.Data.Sqlite;

if (args.Length < 2)
{
    Console.WriteLine("Usage: SqlRunner <database_path> <sql_file_path>");
    return 1;
}

var dbPath = args[0];
var sqlPath = args[1];

if (!File.Exists(dbPath))
{
    Console.WriteLine($"Error: Database file not found: {dbPath}");
    return 1;
}

if (!File.Exists(sqlPath))
{
    Console.WriteLine($"Error: SQL file not found: {sqlPath}");
    return 1;
}

var sql = File.ReadAllText(sqlPath);
var connectionString = $"Data Source={dbPath}";

using var connection = new SqliteConnection(connectionString);
connection.Open();

// Split SQL into individual statements (simple split by semicolon)
var statements = sql.Split(';', StringSplitOptions.RemoveEmptyEntries);

foreach (var statement in statements)
{
    var trimmed = statement.Trim();
    if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("--"))
        continue;

    try
    {
        using var command = connection.CreateCommand();
        command.CommandText = trimmed;

        if (trimmed.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
        {
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    Console.Write(reader.GetValue(i));
                    if (i < reader.FieldCount - 1)
                        Console.Write(" | ");
                }
                Console.WriteLine();
            }
        }
        else
        {
            var rowsAffected = command.ExecuteNonQuery();
            Console.WriteLine($"Rows affected: {rowsAffected}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error executing statement: {ex.Message}");
        Console.WriteLine($"Statement: {trimmed}");
    }
}

Console.WriteLine("SQL script executed successfully.");
return 0;
