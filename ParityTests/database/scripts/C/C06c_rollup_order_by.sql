USE adv;
SELECT 'C06c rollup + ORDER BY', dept, SUM(salary) FROM emp GROUP BY dept WITH ROLLUP ORDER BY dept;
