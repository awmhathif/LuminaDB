DROP USER IF EXISTS 'appn'@'%';
CREATE USER 'appn'@'%' IDENTIFIED BY 'AppN#pass';
SELECT 'C03 created', user, plugin FROM mysql.user WHERE user='appn';
