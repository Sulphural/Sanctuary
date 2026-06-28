-- Check for mounts with invalid Definition (0 or NULL)
SELECT 'Mounts with invalid Definition:' as message;
SELECT Id, CharacterId, Definition, Tint, IsUpgraded
FROM Mounts
WHERE Definition = 0 OR Definition IS NULL;

-- Delete mounts with invalid Definition to prevent serialization issues
DELETE FROM Mounts
WHERE Definition = 0 OR Definition IS NULL;

-- Verify deletion
SELECT 'Remaining mounts after cleanup:' as message;
SELECT Id, CharacterId, Definition, Tint, IsUpgraded
FROM Mounts;
