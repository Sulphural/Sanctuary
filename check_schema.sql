-- Check Mounts table schema
SELECT 'Mounts table schema:' as info;
PRAGMA table_info(Mounts);

-- Check Pets table exists
SELECT 'Checking for Pets table:' as info;
SELECT name FROM sqlite_master WHERE type='table' AND name='Pets';

-- Check applied migrations
SELECT 'Applied migrations:' as info;
SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId;
