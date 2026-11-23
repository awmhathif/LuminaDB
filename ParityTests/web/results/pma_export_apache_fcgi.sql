-- phpMyAdmin SQL Dump
-- version 5.2.3
-- https://www.phpmyadmin.net/
--
-- Host: 127.0.0.1:3510
-- Generation Time: Nov 23, 2026 at 03:06 AM
-- Server version: 11.8.10-MariaDB
-- PHP Version: 8.6.0

SET SQL_MODE = "NO_AUTO_VALUE_ON_ZERO";
SET time_zone = "+00:00";


/*!40101 SET @OLD_CHARACTER_SET_CLIENT=@@CHARACTER_SET_CLIENT */;
/*!40101 SET @OLD_CHARACTER_SET_RESULTS=@@CHARACTER_SET_RESULTS */;
/*!40101 SET @OLD_COLLATION_CONNECTION=@@COLLATION_CONNECTION */;
/*!40101 SET NAMES utf8mb4 */;

--
-- Database: `paritytest`
--

-- --------------------------------------------------------

--
-- Table structure for table `students`
--

CREATE TABLE IF NOT EXISTS `students` (
  `id` int(11) NOT NULL AUTO_INCREMENT,
  `name` varchar(200) NOT NULL,
  `course` varchar(100) NOT NULL DEFAULT '',
  `created_by` varchar(10) NOT NULL,
  `created_at` timestamp NULL DEFAULT current_timestamp(),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

--
-- Dumping data for table `students`
--

INSERT INTO `students` (`id`, `name`, `course`, `created_by`, `created_at`) VALUES
(1, 'ශ්‍රී ලංකා ශිෂ්‍යයා தமிழ் மாணவர் 🎓📚👩🏽‍💻 (mysqli)', 'HNDIT ICT', 'mysqli', '2026-11-22 21:26:55'),
(2, 'test', 'x', 'mysqli', '2026-11-22 21:27:08'),
(3, 'ශ්‍රී ලංකා ශිෂ්‍යයා தமிழ் மாணவர் 🎓📚👩🏽‍💻 (mysqli)', 'HNDIT ICT', 'mysqli', '2026-11-22 21:27:15'),
(4, 'ශ්‍රී ලංකා ශිෂ්‍යයා தமிழ் மாணவர் 🎓📚👩🏽‍💻 (pdo)', 'HNDIT ICT', 'pdo', '2026-11-22 21:27:15'),
(5, 'ශ්‍රී 🎓', 'x', 'mysqli', '2026-11-22 21:27:28'),
(8, 'Form தமிழ் மாணவர்', 'X', 'pdo', '2026-11-22 21:27:35'),
(9, 'pretty 🎓📚👩🏽‍💻', '', 'pdo', '2026-11-22 21:27:36'),
(12, 'Form தமிழ் மாணவர்', 'X', 'pdo', '2026-11-22 21:31:13'),
(13, 'pretty 🎓📚👩🏽‍💻', '', 'pdo', '2026-11-22 21:31:15'),
(16, 'Form தமிழ் மாணவர்', 'X', 'pdo', '2026-11-22 21:34:05'),
(17, 'pretty 🎓📚👩🏽‍💻', '', 'pdo', '2026-11-22 21:34:06'),
(20, 'Form தமிழ் மாணவர்', 'X', 'pdo', '2026-11-22 21:36:08'),
(21, 'pretty 🎓📚👩🏽‍💻', '', 'pdo', '2026-11-22 21:36:09');

-- --------------------------------------------------------

--
-- Table structure for table `pma_made`
--

CREATE TABLE IF NOT EXISTS `pma_made` (
  `id` int(11) NOT NULL,
  `t` varchar(50) DEFAULT NULL,
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_uca1400_ai_ci;

--
-- Dumping data for table `pma_made`
--

INSERT INTO `pma_made` (`id`, `t`) VALUES
(1, 'phpMyAdmin wrote this');

/*!40101 SET CHARACTER_SET_CLIENT=@OLD_CHARACTER_SET_CLIENT */;
/*!40101 SET CHARACTER_SET_RESULTS=@OLD_CHARACTER_SET_RESULTS */;
/*!40101 SET COLLATION_CONNECTION=@OLD_COLLATION_CONNECTION */;
