# PowerShell script to fix pet IDs in the database

Add-Type -AssemblyName "System.Data"

$dbPath = "C:\Users\nadim\Desktop\NEWSanctServer\Sanctuary\src\bin\Release\sanctuary.db"
$connectionString = "Data Source=$dbPath"

try {
    # Load SQLite assembly
    Add-Type -Path "C:\Users\nadim\.nuget\packages\microsoft.data.sqlite.core\10.0.0\lib\net8.0\Microsoft.Data.Sqlite.dll"
    
    $connection = New-Object Microsoft.Data.Sqlite.SqliteConnection($connectionString)
    $connection.Open()
    
    Write-Host "Connected to database"
    
    # Delete old pets
    $deleteCmd = $connection.CreateCommand()
    $deleteCmd.CommandText = "DELETE FROM Pets WHERE CharacterId = 2"
    $deleteCmd.ExecuteNonQuery() | Out-Null
    Write-Host "Deleted old pets"
    
    # Add new pets
    $insertCmd = $connection.CreateCommand()
    $insertCmd.CommandText = "INSERT INTO Pets (CharacterId, Name, Tint, Definition, Created) VALUES (@CharacterId, @Name, @Tint, @Definition, @Created)"
    
    $pets = @(
        @{Name = "Sparkles"; Definition = 1088; Tint = 232},
        @{Name = "Flame"; Definition = 4243; Tint = 227}
    )
    
    foreach ($pet in $pets) {
        $insertCmd.Parameters.Clear()
        $insertCmd.Parameters.AddWithValue("@CharacterId", 2) | Out-Null
        $insertCmd.Parameters.AddWithValue("@Name", $pet.Name) | Out-Null
        $insertCmd.Parameters.AddWithValue("@Tint", $pet.Tint) | Out-Null
        $insertCmd.Parameters.AddWithValue("@Definition", $pet.Definition) | Out-Null
        $insertCmd.Parameters.AddWithValue("@Created", [DateTime]::UtcNow) | Out-Null
        $insertCmd.ExecuteNonQuery() | Out-Null
        Write-Host "Added pet: $($pet.Name) (Definition: $($pet.Definition))"
    }
    
    # Verify
    $selectCmd = $connection.CreateCommand()
    $selectCmd.CommandText = "SELECT Id, Definition, Name, Tint FROM Pets WHERE CharacterId = 2"
    $reader = $selectCmd.ExecuteReader()
    
    Write-Host "`nUpdated pets:"
    while ($reader.Read()) {
        $id = $reader["Id"]
        $def = $reader["Definition"]
        $name = $reader["Name"]
        $tint = $reader["Tint"]
        Write-Host "  ID: $id, Definition: $def, Name: $name, Tint: $tint"
    }
    
    $connection.Close()
    Write-Host "`nDone!"
}
catch {
    Write-Host "Error: $_"
}
