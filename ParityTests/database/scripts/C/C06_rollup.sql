USE adv;
SELECT 'C06 rollup', dept, SUM(salary) FROM emp GROUP BY dept WITH ROLLUP;
SELECT 'C06b GROUPING()', dept, GROUPING(dept), SUM(salary) FROM emp GROUP BY dept WITH ROLLUP;
SELECT 'C06c rollup + ORDER BY', dept, SUM(salary) FROM emp GROUP BY dept WITH ROLLUP ORDER BY dept;
