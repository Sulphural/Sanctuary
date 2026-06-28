-- SQLite version to add test pets to character ID 2
-- Make sure your character ID is 2 (check server logs)

-- Delete any existing test pets first
DELETE FROM Pets WHERE CharacterId = 2;

-- Add a Fluffy Dog pet (Definition Id: 20001)
INSERT INTO Pets (CharacterId, Name, Tint, Definition, Created)
VALUES (2, 'Sparkles', 0, 20001, datetime('now'));

-- Add an Orange Tabby Cat pet (Definition Id: 20003)
INSERT INTO Pets (CharacterId, Name, Tint, Definition, Created)
VALUES (2, 'Robo', 0, 20003, datetime('now'));

-- Add a Baby Dragon (Red) pet (Definition Id: 20005)
INSERT INTO Pets (CharacterId, Name, Tint, Definition, Created)
VALUES (2, 'Flame', 0, 20005, datetime('now'));

-- Verify the pets were added
SELECT * FROM Pets WHERE CharacterId = 2;
