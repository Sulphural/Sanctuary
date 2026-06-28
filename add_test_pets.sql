-- Sample SQL script to add test pets to characters
-- Replace CHARACTER_ID with your actual character ID
-- IMPORTANT: Definition IDs must exist in src/Resources/Pets.json

-- Add a Fluffy Dog pet (Definition Id: 20001)
INSERT INTO Pets (Id, CharacterGuid, Name, Tint, Definition, Created)
VALUES (1, CHARACTER_ID, 'Sparkles', 0, 20001, NOW());

-- Add an Orange Tabby Cat pet (Definition Id: 20003)
INSERT INTO Pets (Id, CharacterGuid, Name, Tint, Definition, Created)
VALUES (2, CHARACTER_ID, 'Robo', 0, 20003, NOW());

-- Add a Baby Dragon (Red) pet (Definition Id: 20005)
INSERT INTO Pets (Id, CharacterGuid, Name, Tint, Definition, Created)
VALUES (3, CHARACTER_ID, 'Flame', 0, 20005, NOW());

-- Example to add a pet with custom name and tint:
-- INSERT INTO Pets (Id, CharacterGuid, Name, Tint, Definition, Created)
-- VALUES (4, CHARACTER_ID, 'Custom Pet Name', 255, 20001, NOW());

-- NOTE (SQLite users): Column is CharacterId instead of CharacterGuid and NOW() should be datetime('now')
-- Example for SQLite:
-- INSERT INTO Pets (Id, CharacterId, Name, Tint, Definition, Created)
-- VALUES (1, CHARACTER_ID, 'Sparkles', 0, 20001, datetime('now'));
