#!/usr/bin/env dotnet-script
#r "nuget: BCrypt.Net-Next, 4.0.3"

using BCrypt.Net;

// Usage: dotnet script ResetPassword.csx "newpassword"
// Or just run it and modify the password variable below

var password = Args.Length > 0 ? Args[0] : "newpassword123";

var salt = BCrypt.GenerateSalt();
var hashedPassword = BCrypt.HashPassword(password, salt);

Console.WriteLine("Password: " + password);
Console.WriteLine("BCrypt Hash: " + hashedPassword);
Console.WriteLine();
Console.WriteLine("SQL Command:");
Console.WriteLine($"UPDATE Users SET Password = '{hashedPassword}' WHERE Username = 'your_username';");
