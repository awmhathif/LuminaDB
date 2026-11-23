DROP USER IF EXISTS 'appn'@'%';
CREATE USER 'appn'@'%' IDENTIFIED WITH mysql_native_password BY 'AppN#pass';
SELECT 'C03 created', user, plugin FROM mysql.user WHERE user='appn';
