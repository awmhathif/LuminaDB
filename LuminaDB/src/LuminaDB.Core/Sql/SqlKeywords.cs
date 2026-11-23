using System;
using System.Collections.Generic;
using System.Linq;

namespace LuminaDB.Core.Sql
{
    /// <summary>Word lists for checking, highlighting, completion and tidying.</summary>
    public static class SqlKeywords
    {
        private static HashSet<string> Set(string words) =>
            new HashSet<string>(words.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);

        /// <summary>Words that may begin a command.</summary>
        public static readonly HashSet<string> StatementStarters = Set(@"
SELECT INSERT UPDATE DELETE REPLACE CREATE ALTER DROP TRUNCATE RENAME USE SHOW DESCRIBE DESC EXPLAIN
SET BEGIN START COMMIT ROLLBACK SAVEPOINT RELEASE GRANT REVOKE CALL DO HANDLER LOAD LOCK UNLOCK WITH
VALUES TABLE ANALYZE OPTIMIZE CHECK REPAIR CHECKSUM FLUSH KILL PREPARE EXECUTE DEALLOCATE DECLARE HELP
INSTALL UNINSTALL RESET PURGE XA BACKUP SIGNAL RESIGNAL GET CACHE SHUTDOWN BINLOG CHANGE IF WHILE REPEAT
LOOP LEAVE ITERATE RETURN OPEN FETCH CLOSE CASE");

        /// <summary>Starters suggested by "did you mean" (the common ones only).</summary>
        public static readonly string[] CommonStarters =
        {
            "SELECT", "INSERT", "UPDATE", "DELETE", "REPLACE", "CREATE", "ALTER", "DROP", "TRUNCATE", "RENAME",
            "USE", "SHOW", "DESCRIBE", "EXPLAIN", "SET", "BEGIN", "START", "COMMIT", "ROLLBACK", "GRANT", "REVOKE", "CALL", "WITH",
        };

        /// <summary>Starters that mean "a new command begins here" when found at the start of a line.</summary>
        public static readonly HashSet<string> LineStarters = Set("SELECT INSERT UPDATE DELETE CREATE DROP ALTER USE SHOW TRUNCATE DESCRIBE REPLACE");

        /// <summary>Clause words a typo is often made in (FORM, WHER, VALUSE...).</summary>
        public static readonly string[] ClauseWords =
        {
            "SELECT", "FROM", "WHERE", "GROUP", "ORDER", "HAVING", "LIMIT", "VALUES", "INTO", "JOIN", "INNER", "LEFT", "RIGHT",
            "UPDATE", "DELETE", "INSERT", "TABLE", "CREATE", "DISTINCT", "BETWEEN", "PRIMARY", "FOREIGN", "REFERENCES", "DEFAULT",
        };

        /// <summary>MariaDB reserved words: unusable as plain names without backticks.</summary>
        public static readonly HashSet<string> Reserved = Set(@"
ACCESSIBLE ADD ALL ALTER ANALYZE AND AS ASC ASENSITIVE BEFORE BETWEEN BIGINT BINARY BLOB BOTH BY CALL CASCADE
CASE CHANGE CHAR CHARACTER CHECK COLLATE COLUMN CONDITION CONSTRAINT CONTINUE CONVERT CREATE CROSS CURRENT_DATE
CURRENT_ROLE CURRENT_TIME CURRENT_TIMESTAMP CURRENT_USER CURSOR DATABASE DATABASES DAY_HOUR DAY_MICROSECOND
DAY_MINUTE DAY_SECOND DEC DECIMAL DECLARE DEFAULT DELAYED DELETE DELETE_DOMAIN_ID DESC DESCRIBE DETERMINISTIC
DISTINCT DISTINCTROW DIV DO_DOMAIN_IDS DOUBLE DROP DUAL EACH ELSE ELSEIF ENCLOSED ESCAPED EXCEPT EXISTS EXIT
EXPLAIN FALSE FETCH FLOAT FLOAT4 FLOAT8 FOR FORCE FOREIGN FROM FULLTEXT GENERAL GRANT GROUP HAVING HIGH_PRIORITY
HOUR_MICROSECOND HOUR_MINUTE HOUR_SECOND IF IGNORE IGNORE_DOMAIN_IDS IGNORE_SERVER_IDS IN INDEX INFILE INNER
INOUT INSENSITIVE INSERT INT INT1 INT2 INT3 INT4 INT8 INTEGER INTERSECT INTERVAL INTO IS ITERATE JOIN KEY KEYS
KILL LEADING LEAVE LEFT LIKE LIMIT LINEAR LINES LOAD LOCALTIME LOCALTIMESTAMP LOCK LONG LONGBLOB LONGTEXT LOOP
LOW_PRIORITY MASTER_HEARTBEAT_PERIOD MASTER_SSL_VERIFY_SERVER_CERT MATCH MAXVALUE MEDIUMBLOB MEDIUMINT MEDIUMTEXT
MIDDLEINT MINUTE_MICROSECOND MINUTE_SECOND MOD MODIFIES NATURAL NOT NO_WRITE_TO_BINLOG NULL NUMERIC OFFSET ON
OPTIMIZE OPTION OPTIONALLY OR ORDER OUT OUTER OUTFILE OVER PAGE_CHECKSUM PARSE_VCOL_EXPR PARTITION POSITION
PRECISION PRIMARY PROCEDURE PURGE RANGE READ READS READ_WRITE REAL RECURSIVE REF_SYSTEM_ID REFERENCES REGEXP
RELEASE RENAME REPEAT REPLACE REQUIRE RESIGNAL RESTRICT RETURN RETURNING REVOKE RIGHT RLIKE ROW_NUMBER ROWS
SCHEMA SCHEMAS SECOND_MICROSECOND SELECT SENSITIVE SEPARATOR SET SHOW SIGNAL SLOW SMALLINT SPATIAL SPECIFIC SQL
SQLEXCEPTION SQLSTATE SQLWARNING SQL_BIG_RESULT SQL_CALC_FOUND_ROWS SQL_SMALL_RESULT SSL STARTING STATS_AUTO_RECALC
STATS_PERSISTENT STATS_SAMPLE_PAGES STRAIGHT_JOIN TABLE TERMINATED THEN TINYBLOB TINYINT TINYTEXT TO TRAILING
TRIGGER TRUE UNDO UNION UNIQUE UNLOCK UNSIGNED UPDATE USAGE USE USING UTC_DATE UTC_TIME UTC_TIMESTAMP VALUES
VARBINARY VARCHAR VARCHARACTER VARYING WHEN WHERE WHILE WINDOW WITH WRITE XOR YEAR_MONTH ZEROFILL");

        /// <summary>Non-reserved words with a meaning in SQL syntax (never column-name suspects).</summary>
        public static readonly HashSet<string> Keywords = Set(@"
ACTION AFTER AGAINST ALGORITHM ALWAYS ANY AT ATOMIC AUTO_INCREMENT AUTOCOMMIT AVG_ROW_LENGTH BEGIN BOOLEAN BOOL
BTREE CASCADED CHAIN CHARSET CHECKSUM COLLATION COLUMNS COMMENT COMMIT COMMITTED COMPACT COMPRESSED CONCURRENT
CONSISTENT CONTAINS CURRENT DATA DEFINER DELAY_KEY_WRITE DISABLE DISCARD DO DUPLICATE DYNAMIC ENABLE END ENGINE
ENGINES ENUM ERRORS ESCAPE EVENT EVENTS EVERY EXCHANGE EXECUTE EXPANSION EXTENDED FIELDS FIRST FIXED FLUSH
FOLLOWING FORMAT FULL FUNCTION GLOBAL GRANTS HANDLER HASH HELP IDENTIFIED IMPORT INDEXES INPLACE INSTANT INVISIBLE
INVOKER ISOLATION JSON KEY_BLOCK_SIZE LAST LESS LEVEL LOCAL LOCKED LOGS MAX_ROWS MEMORY MERGE MIN_ROWS MODE MODIFY
NAMES NATIONAL NEXT NO NONE NOWAIT NULLS ONLY OPEN OPTIONS OWNER PACK_KEYS PARSER PARTIAL PARTITIONS PASSWORD
PERSISTENT PLUGINS PRECEDING PREPARE PREV PRIVILEGES PROCESS PROCESSLIST QUICK REBUILD REDUNDANT RELOAD REMOVE
REORGANIZE REPAIR REPEATABLE REPLICA RESET RESTART RESUME RETURNS ROLE ROLLBACK ROLLUP ROUTINE ROW ROW_FORMAT
SAVEPOINT SECURITY SEQUENCE SERIAL SERIALIZABLE SESSION SHARE SHUTDOWN SIGNED SIMPLE SKIP SNAPSHOT SOME SONAME
SQL_NO_CACHE SQL_CACHE START STARTS STATUS STOP STORAGE STORED SUBPARTITION SUPER SUSPEND SYSTEM SYSTEM_TIME
TABLES TABLESPACE TEMPORARY TEMPTABLE TEXT THAN TIES TRANSACTION TRIGGERS TRUNCATE TYPE UNBOUNDED UNCOMMITTED
UNDEFINED UNKNOWN UPGRADE USER VALIDATION VALUE VARIABLES VERSIONING VIEW VIRTUAL VISIBLE WAIT WARNINGS WITHOUT
WORK WRAPPER XA XML GENERATED UNDO_BUFFER_SIZE INNODB MYISAM ARIA CSV UTF8 UTF8MB3 UTF8MB4 LATIN1 ASCII CHAR_LENGTH
PERIOD ELSIF PACKAGE BODY OTHERS EXCEPTION RAISE ROWTYPE OF HISTORY CONTAINS RESULT ADMIN EXPIRE NEVER ACCOUNT
UNLOCKED CURRENT_PATH SQL_SAFE_UPDATES FOREIGN_KEY_CHECKS UNIQUE_CHECKS GLOBAL SESSION");

        /// <summary>Data type names.</summary>
        public static readonly HashSet<string> Types = Set(@"
INT INTEGER TINYINT SMALLINT MEDIUMINT BIGINT DECIMAL DEC NUMERIC FIXED FLOAT DOUBLE REAL BIT BOOL BOOLEAN SERIAL
CHAR VARCHAR BINARY VARBINARY TINYTEXT TEXT MEDIUMTEXT LONGTEXT TINYBLOB BLOB MEDIUMBLOB LONGBLOB ENUM SET JSON
DATE TIME DATETIME TIMESTAMP YEAR GEOMETRY POINT LINESTRING POLYGON UUID INET4 INET6 UNSIGNED ZEROFILL SIGNED NCHAR NVARCHAR");

        /// <summary>Interval/date-part words.</summary>
        public static readonly HashSet<string> DateParts = Set(@"
MICROSECOND SECOND MINUTE HOUR DAY WEEK MONTH QUARTER YEAR SECOND_MICROSECOND MINUTE_SECOND HOUR_MINUTE DAY_HOUR YEAR_MONTH");

        /// <summary>Built-in functions (call syntax: name directly followed by "(").</summary>
        public static readonly HashSet<string> Functions = Set(@"
ABS ACOS ADDDATE ADDTIME AES_DECRYPT AES_ENCRYPT ANY_VALUE ASCII ASIN ATAN ATAN2 AVG BENCHMARK BIN BIT_AND BIT_COUNT
BIT_LENGTH BIT_OR BIT_XOR CAST CEIL CEILING CHAR_LENGTH CHARACTER_LENGTH COALESCE CONCAT CONCAT_WS CONNECTION_ID CONV
CONVERT_TZ COS COT COUNT CRC32 CUME_DIST CURDATE CURRENT_DATE CURRENT_TIME CURRENT_TIMESTAMP CURTIME DATABASE DATE_ADD
DATE_FORMAT DATE_SUB DATEDIFF DAYNAME DAYOFMONTH DAYOFWEEK DAYOFYEAR DEGREES DENSE_RANK ELT EXP EXTRACT FIELD
FIND_IN_SET FIRST_VALUE FLOOR FORMAT FOUND_ROWS FROM_BASE64 FROM_DAYS FROM_UNIXTIME GREATEST GROUP_CONCAT HEX IFNULL
INET_ATON INET_NTOA INSTR ISNULL JSON_ARRAY JSON_ARRAYAGG JSON_CONTAINS JSON_EXTRACT JSON_OBJECT JSON_OBJECTAGG
JSON_UNQUOTE JSON_VALUE JSON_SET JSON_REMOVE JSON_LENGTH JSON_KEYS JSON_VALID LAG LAST_DAY LAST_INSERT_ID LAST_VALUE
LCASE LEAD LEAST LENGTH LN LOCATE LOG LOG10 LOG2 LOWER LPAD LTRIM MAKEDATE MAKETIME MAX MD5 MEDIAN MICROSECOND MID MIN
MONTHNAME NOW NTILE NTH_VALUE NULLIF OCT OCTET_LENGTH ORD PERCENT_RANK PERIOD_ADD PERIOD_DIFF PI POW POWER QUARTER
QUOTE RADIANS RAND RANK REGEXP_REPLACE REGEXP_SUBSTR REGEXP_INSTR REPEAT REVERSE ROUND ROW_COUNT ROW_NUMBER RPAD
RTRIM SEC_TO_TIME SHA SHA1 SHA2 SIGN SIN SLEEP SOUNDEX SPACE SQRT STD STDDEV STDDEV_POP STDDEV_SAMP STR_TO_DATE STRCMP
SUBDATE SUBSTR SUBSTRING SUBSTRING_INDEX SUBTIME SUM SYSDATE TAN TIME_FORMAT TIME_TO_SEC TIMEDIFF TIMESTAMPADD
TIMESTAMPDIFF TO_BASE64 TO_DAYS TO_SECONDS TRIM TRUNCATE UCASE UNHEX UNIX_TIMESTAMP UPPER USER UTC_DATE UTC_TIME
UTC_TIMESTAMP UUID UUID_SHORT VAR_POP VAR_SAMP VARIANCE VERSION WEEKDAY WEEKOFYEAR YEARWEEK IF REPLACE LEFT RIGHT
INSERT DATE TIME YEAR MONTH DAY HOUR MINUTE SECOND WEEK TIMESTAMP CHAR VALUES INTERVAL MOD PASSWORD SCHEMA
CURRENT_USER SESSION_USER SYSTEM_USER LOCALTIME LOCALTIMESTAMP NEXTVAL LASTVAL SETVAL ST_ASTEXT ST_GEOMFROMTEXT
POINT JSON_TABLE JSON_QUERY JSON_INSERT JSON_REPLACE JSON_MERGE JSON_MERGE_PATCH JSON_TYPE JSON_DEPTH JSON_SEARCH
COLUMN_JSON CHR NVL NVL2 DECODE TO_CHAR SFORMAT NATURAL_SORT_KEY KDF");

        /// <summary>Literal-like words (TRUE, NULL...).</summary>
        public static readonly HashSet<string> Literals = Set("NULL TRUE FALSE UNKNOWN DEFAULT");

        /// <summary>Anything that is SQL vocabulary rather than one of the user's names.</summary>
        public static bool IsVocabulary(string word) =>
            Reserved.Contains(word) || Keywords.Contains(word) || Types.Contains(word) || Functions.Contains(word) ||
            DateParts.Contains(word) || Literals.Contains(word) || StatementStarters.Contains(word);

        /// <summary>Words the formatter writes in capitals.</summary>
        public static bool IsUppercaseKeyword(string word) =>
            Reserved.Contains(word) || Types.Contains(word) || Literals.Contains(word) || StatementStarters.Contains(word) ||
            Keywords.Contains(word) && !NeverUpper.Contains(word);

        private static readonly HashSet<string> NeverUpper = Set("UTF8 UTF8MB3 UTF8MB4 LATIN1 ASCII INNODB MYISAM ARIA CSV");

        /// <summary>Everything suggested by autocomplete, upper-case.</summary>
        public static IEnumerable<string> CompletionWords()
        {
            var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var w in StatementStarters) all.Add(w);
            foreach (var w in Reserved) all.Add(w);
            foreach (var w in Keywords) all.Add(w);
            foreach (var w in Types) all.Add(w);
            foreach (var w in Functions) all.Add(w);
            return all.Select(w => w.ToUpperInvariant()).OrderBy(w => w, StringComparer.Ordinal);
        }

        /// <summary>Optimal string alignment distance (typos incl. swapped letters), case-insensitive.</summary>
        public static int Distance(string a, string b)
        {
            a = (a ?? "").ToUpperInvariant();
            b = (b ?? "").ToUpperInvariant();
            int n = a.Length, m = b.Length;
            if (n == 0) return m;
            if (m == 0) return n;
            var d = new int[n + 1, m + 1];
            for (int i = 0; i <= n; i++) d[i, 0] = i;
            for (int j = 0; j <= m; j++) d[0, j] = j;
            for (int i = 1; i <= n; i++)
                for (int j = 1; j <= m; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    int v = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                    if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) v = Math.Min(v, d[i - 2, j - 2] + 1);
                    d[i, j] = v;
                }
            return d[n, m];
        }

        /// <summary>Closest candidate within a sensible typo distance, or null.</summary>
        public static string Closest(string word, IEnumerable<string> candidates, int? maxDistance = null)
        {
            if (string.IsNullOrEmpty(word) || candidates == null) return null;
            int limit = maxDistance ?? (word.Length <= 3 ? 1 : word.Length <= 6 ? 2 : 3);
            string best = null;
            int bestD = int.MaxValue;
            foreach (var c in candidates)
            {
                if (string.IsNullOrEmpty(c)) continue;
                if (string.Equals(c, word, StringComparison.OrdinalIgnoreCase)) continue;
                int d = Distance(word, c);
                // plural/singular slips (student vs students) count as one step
                if (string.Equals(c + "s", word, StringComparison.OrdinalIgnoreCase) || string.Equals(word + "s", c, StringComparison.OrdinalIgnoreCase)) d = Math.Min(d, 1);
                if (d < bestD || d == bestD && best != null && string.CompareOrdinal(c, best) < 0) { bestD = d; best = c; }
            }
            return bestD <= limit ? best : null;
        }
    }
}
