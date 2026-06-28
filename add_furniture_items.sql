-- Add test furniture items to player inventory for housing testing
-- These are generic items that can be placed as furniture

-- Insert furniture items (adjust character ID as needed)
-- Item IDs are examples - replace with actual furniture item IDs from your game

INSERT INTO Items (CharacterId, Definition, Count, Created)
VALUES 
    (5, 1000, 10, datetime('now')),  -- Example furniture item 1
    (5, 1001, 10, datetime('now')),  -- Example furniture item 2
    (5, 1002, 10, datetime('now')),  -- Example furniture item 3
    (5, 1003, 10, datetime('now')),  -- Example furniture item 4
    (5, 1004, 10, datetime('now'));  -- Example furniture item 5

-- Query to verify items were added
SELECT * FROM Items WHERE CharacterId = 5 AND Definition >= 1000 AND Definition <= 1004;
