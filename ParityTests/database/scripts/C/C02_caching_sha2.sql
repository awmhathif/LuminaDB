DROP USER IF EXISTS 'app8'@'%';
CREATE USER 'app8'@'%' IDENTIFIED WITH caching_sha2_password BY 'App8#pass';
SELECT 'C02 created', user, plugin FROM mysql.user WHERE user='app8';
