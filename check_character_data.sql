-- Check mounts for character 2
SELECT * FROM Mounts WHERE CharacterId = 2;

-- Check pets for character 2
SELECT * FROM Pets WHERE CharacterId = 2;

-- Check items for character 2 (first 10)
SELECT Id, CharacterId, Definition, Tint, Count FROM Items WHERE CharacterId = 2 LIMIT 10;
