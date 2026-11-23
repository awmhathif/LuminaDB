USE school;
SELECT 'C07 lateral', d.name, x.top FROM dept d, LATERAL (SELECT MAX(s.age) top FROM student s WHERE s.dept_id=d.id) AS x;
