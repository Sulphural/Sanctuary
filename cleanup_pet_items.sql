-- Remove pet items from Items table
-- Item 10481 is Phoenix pet egg
DELETE FROM Items WHERE Definition = 10481;

-- If you bought other pets, also remove their items:
-- DELETE FROM Items WHERE Definition IN (10481, 2171, ...);

SELECT * FROM Items;
SELECT * FROM Pets;
