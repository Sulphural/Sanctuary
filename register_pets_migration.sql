-- Register the Pets migration as applied
INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion)
VALUES ('20251210000000_AddPets', '9.0.9');

-- Verify it was added
SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId;
