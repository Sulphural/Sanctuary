-- Fix pet definition IDs to match Pets.json
-- Change pet definitions from 1 and 2 to 1088 (Wolf) and 4243 (Lava Tiger)

DELETE FROM Pets WHERE CharacterId = 2;

INSERT INTO Pets (CharacterId, Name, Tint, Definition, Created)
VALUES (2, 'Sparkles', 232, 1088, datetime('now'));

INSERT INTO Pets (CharacterId, Name, Tint, Definition, Created)
VALUES (2, 'Flame', 227, 4243, datetime('now'));

-- Verify
SELECT Id, Definition, Name, Tint FROM Pets WHERE CharacterId = 2;
