DROP DATABASE IF EXISTS deflt; CREATE DATABASE deflt; USE deflt;
CREATE TABLE plain (s VARCHAR(50)); CREATE TABLE explicit_cs (s VARCHAR(50)) CHARSET utf8mb4;
INSERT INTO plain VALUES ('ශ්‍රී ලංකාව 😀');
SELECT 'V03 db/table collations', (SELECT DEFAULT_COLLATION_NAME FROM information_schema.schemata WHERE schema_name='deflt'), GROUP_CONCAT(CONCAT(table_name,'=',table_collation)) FROM information_schema.tables WHERE table_schema='deflt';
SELECT 'V03b sinhala+emoji in default table byte-exact', HEX(s)=HEX(_utf8mb4 X'E0B781E0B78AE2808DE0B6BBE0B79320E0B6BDE0B682E0B69AE0B78FE0B78020F09F9880') FROM plain;
-- what an UNCONFIGURED 10.4 does (server default latin1): simulate with explicit latin1 table, strict mode on
CREATE TABLE latin (s VARCHAR(50)) CHARSET latin1;
INSERT INTO latin VALUES ('ශ්‍රී ලංකාව');
SELECT 'V03c latin1 table (10.4 factory default) stores', HEX(s), s FROM latin;
