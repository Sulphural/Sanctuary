# PowerShell script to add test pets to the database
# Run this with: powershell -ExecutionPolicy Bypass -File AddTestPets.ps1

$dbPath = ".\src\bin\Release\sanctuary.db"
$characterId = 2  # Change this to match your character ID from the server logs

Write-Host "Adding test pets to character ID $characterId..." -ForegroundColor Cyan
Write-Host "Database: $dbPath" -ForegroundColor Gray

if (!(Test-Path $dbPath)) {
    Write-Host "ERROR: Database not found at $dbPath" -ForegroundColor Red
    exit 1
}

# Load SQLite assembly
Add-Type -Path ".\src\bin\Release\Microsoft.Data.Sqlite.dll"

try {
    $connectionString = "Data Source=$dbPath"
    $connection = New-Object Microsoft.Data.Sqlite.SqliteConnection($connectionString)
    $connection.Open()

    # Check if character exists
    $checkCmd = $connection.CreateCommand()
    $checkCmd.CommandText = "SELECT Name FROM Characters WHERE Id = @CharacterId"
    $checkCmd.Parameters.AddWithValue("@CharacterId", $characterId) | Out-Null
    $charName = $checkCmd.ExecuteScalar()

    if ($null -eq $charName) {
        Write-Host "ERROR: Character with ID $characterId not found!" -ForegroundColor Red
        Write-Host "Available characters:" -ForegroundColor Yellow

        $listCmd = $connection.CreateCommand()
        $listCmd.CommandText = "SELECT Id, Name FROM Characters"
        $reader = $listCmd.ExecuteReader()

        while ($reader.Read()) {
            Write-Host "  ID: $($reader.GetInt32(0)), Name: $($reader.GetString(1))"
        }
        $reader.Close()
        $connection.Close()
        exit 1
    }

    Write-Host "Found character: $charName (ID: $characterId)" -ForegroundColor Green

    # Delete existing pets
    $deleteCmd = $connection.CreateCommand()
    $deleteCmd.CommandText = "DELETE FROM Pets WHERE CharacterId = @CharacterId"
    $deleteCmd.Parameters.AddWithValue("@CharacterId", $characterId) | Out-Null
    $deleted = $deleteCmd.ExecuteNonQuery()
    Write-Host "Deleted $deleted existing pets" -ForegroundColor Yellow

    # Add test pets
    $pets = @(
        @{Name="Sparkles"; Definition=20001; Tint=0},
        @{Name="Robo"; Definition=20003; Tint=0},
        @{Name="Flame"; Definition=20005; Tint=0}
    )

    foreach ($pet in $pets) {
        $insertCmd = $connection.CreateCommand()
        $insertCmd.CommandText = @"
            INSERT INTO Pets (CharacterId, Name, Tint, Definition, Created)
            VALUES (@CharacterId, @Name, @Tint, @Definition, datetime('now'))
"@
        $insertCmd.Parameters.AddWithValue("@CharacterId", $characterId) | Out-Null
        $insertCmd.Parameters.AddWithValue("@Name", $pet.Name) | Out-Null
        $insertCmd.Parameters.AddWithValue("@Tint", $pet.Tint) | Out-Null
        $insertCmd.Parameters.AddWithValue("@Definition", $pet.Definition) | Out-Null

        $insertCmd.ExecuteNonQuery() | Out-Null
        Write-Host "Added pet: $($pet.Name) (Definition: $($pet.Definition))" -ForegroundColor Green
    }

    # Verify
    $verifyCmd = $connection.CreateCommand()
    $verifyCmd.CommandText = "SELECT COUNT(*) FROM Pets WHERE CharacterId = @CharacterId"
    $verifyCmd.Parameters.AddWithValue("@CharacterId", $characterId) | Out-Null
    $count = $verifyCmd.ExecuteScalar()

    $connection.Close()

    Write-Host "`nSuccess! Character now has $count pets." -ForegroundColor Green
    Write-Host "Restart your server and log in to test." -ForegroundColor Cyan
}
catch {
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host $_.Exception.StackTrace -ForegroundColor DarkRed
    exit 1
}
