-- Workbench emits INVISIBLE when the "Visible" box is unticked on an index
CREATE DATABASE IF NOT EXISTS c8; USE c8;
DROP TABLE IF EXISTS tinvidx;
CREATE TABLE IF NOT EXISTS `c8`.`tinvidx` (
  `id` INT NOT NULL,
  `d` DATE NULL,
  PRIMARY KEY (`id`),
  INDEX `date_idx` (`d` DESC) IGNORED)
ENGINE = InnoDB;
SELECT 'C01b ok';
