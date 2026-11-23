USE library_db;
SELECT 'D tables', GROUP_CONCAT(CONCAT(table_name,':',table_collation) ORDER BY table_name) FROM information_schema.tables WHERE table_schema='library_db';
SELECT 'D rowcounts', (SELECT COUNT(*) FROM authors),(SELECT COUNT(*) FROM books),(SELECT COUNT(*) FROM loans);
SELECT 'D book id 0 kept', COUNT(*) FROM books WHERE book_id=0;
SELECT 'D auto_increment next', table_name, AUTO_INCREMENT FROM information_schema.tables WHERE table_schema='library_db' ORDER BY table_name;
SELECT 'D FKs', constraint_name, table_name, referenced_table_name, delete_rule FROM information_schema.referential_constraints WHERE constraint_schema='library_db' ORDER BY constraint_name;
SELECT 'D sinhala hex ok', IF(HEX(name)=HEX(CONVERT(_utf8mb4 X'E0B69AE0B794E0B6B8E0B78FE0B6BBE0B6ADE0B794E0B682E0B69C20E0B6B8E0B794E0B6B1E0B792E0B6AFE0B78FE0B783' USING utf8mb4)),'PASS','FAIL'), name FROM authors WHERE author_id=4;
INSERT INTO authors(name) VALUES ('New'); SELECT 'D new author id (expect 5)', LAST_INSERT_ID();
