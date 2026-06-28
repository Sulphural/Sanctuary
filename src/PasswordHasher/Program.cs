using System;
using BC = BCrypt.Net.BCrypt;

Console.WriteLine("=== BCrypt Password Hash Generator ===");
Console.WriteLine();

if (args.Length > 0)
{
    // Use command-line argument
    var password = args[0];
    var hash = BC.HashPassword(password);

    Console.WriteLine($"Password: {password}");
    Console.WriteLine($"BCrypt Hash: {hash}");
    Console.WriteLine();
    Console.WriteLine("SQL Command:");
    Console.WriteLine($"UPDATE Users SET Password = '{hash}' WHERE Username = 'your_username';");
}
else
{
    // Interactive mode
    Console.Write("Enter new password: ");
    var password = Console.ReadLine();

    if (string.IsNullOrEmpty(password))
    {
        Console.WriteLine("Password cannot be empty!");
        return;
    }

    var hash = BC.HashPassword(password);

    Console.WriteLine();
    Console.WriteLine($"BCrypt Hash: {hash}");
    Console.WriteLine();
    Console.WriteLine("SQL Command:");
    Console.WriteLine($"UPDATE Users SET Password = '{hash}' WHERE Username = 'your_username';");
}

Console.WriteLine();
Console.WriteLine("Press any key to exit...");
Console.ReadKey();
