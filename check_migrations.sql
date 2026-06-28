-- Check applied migrations
SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId;

-- Check if Pets table exists
SELECT name, sql FROM sqlite_master WHERE type='table' AND name='Pets';

-- Check Mounts table structure
SELECT sql FROM sqlite_master WHERE type='table' AND name='Mounts';
