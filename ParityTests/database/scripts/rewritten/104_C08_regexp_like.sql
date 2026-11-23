USE school;
SELECT 'C08 regexp_like', name FROM student WHERE (name REGEXP '^[KN]');
SELECT 'C08b regexp_replace/substr/instr', REGEXP_REPLACE('a1b2','[0-9]','#'), REGEXP_SUBSTR('abc123','[0-9]+'), REGEXP_INSTR('abc123','[0-9]');
SELECT 'C08c REGEXP operator', name FROM student WHERE name REGEXP '^[KN]';
