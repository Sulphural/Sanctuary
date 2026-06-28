-- SQLite migration to add Pets table
-- Run this script on your sanctuary.db database

-- Add missing columns to Mounts table
ALTER TABLE Mounts ADD COLUMN Tint INTEGER NOT NULL DEFAULT 0;
ALTER TABLE Mounts ADD COLUMN Definition INTEGER NOT NULL DEFAULT 0;

-- Create Pets table
CREATE TABLE IF NOT EXISTS Pets (
    Id INTEGER NOT NULL,
    CharacterId INTEGER NOT NULL,
    Name TEXT NOT NULL,
    Tint INTEGER NOT NULL,
    Definition INTEGER NOT NULL,
    Created TEXT NOT NULL DEFAULT (datetime('now')),
    CONSTRAINT PK_Pets PRIMARY KEY (Id, CharacterId),
    CONSTRAINT FK_Pets_Characters_CharacterId FOREIGN KEY (CharacterId) REFERENCES Characters (Id) ON DELETE CASCADE
);

-- Create indexes
CREATE UNIQUE INDEX IF NOT EXISTS IX_Mounts_Tint_Definition_CharacterId ON Mounts (Tint, Definition, CharacterId);
CREATE INDEX IF NOT EXISTS IX_Pets_CharacterId ON Pets (CharacterId);
CREATE UNIQUE INDEX IF NOT EXISTS IX_Pets_Tint_Definition_CharacterId ON Pets (Tint, Definition, CharacterId);
