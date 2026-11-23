using System;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MySqlConnector;

// Section G: MySqlConnector 2.4.0 against a portable MariaDB.  usage: ConnTest.exe <port>
internal static class Program
{
    static int fails;

    static void Report(string id, bool ok, string detail)
    {
        if (!ok) fails++;
        Console.WriteLine($"{id}\t{(ok ? "PASS" : "FAIL")}\t{detail}");
    }

    static void Try(string id, Action a)
    {
        try { a(); }
        catch (Exception ex) { Report(id, false, ex.GetType().Name + ": " + ex.Message.Replace("\n", " ")); }
    }

    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        int port = int.Parse(args[0]);
        string rootCs = $"Server=127.0.0.1;Port={port};User ID=root;Password=;CharacterSet=utf8mb4;AllowUserVariables=true";

        // setup: a password user
        using (var c = new MySqlConnection(rootCs))
        {
            c.Open();
            Console.WriteLine($"G00\tINFO\tServerVersion={c.ServerVersion}");
            Exec(c, "DROP USER IF EXISTS 'gtest'@'%'");
            Exec(c, "CREATE USER 'gtest'@'%' IDENTIFIED BY 'G#pass123'");
            Exec(c, "GRANT ALL ON adv.* TO 'gtest'@'%'");
            Exec(c, "DROP TABLE IF EXISTS adv.g_utf");
            Exec(c, "CREATE TABLE adv.g_utf (id INT AUTO_INCREMENT PRIMARY KEY, s VARCHAR(200)) CHARSET utf8mb4 COLLATE utf8mb4_unicode_ci");
        }

        string cs = $"Server=127.0.0.1;Port={port};User ID=gtest;Password=G#pass123;Database=adv;CharacterSet=utf8mb4";

        Try("G01 password user connect", () =>
        {
            using var c = new MySqlConnection(cs); c.Open();
            var who = Scalar(c, "SELECT CURRENT_USER()");
            var cipher = Scalar(c, "SELECT VARIABLE_VALUE FROM information_schema.SESSION_STATUS WHERE VARIABLE_NAME='Ssl_cipher'");
            Report("G01 password user connect", who.ToString() == "gtest@%", $"current_user={who}; TLS cipher='{cipher}' (SslMode default=Preferred)");
        });
        Try("G01b wrong password rejected", () =>
        {
            try { using var c = new MySqlConnection(cs.Replace("G#pass123", "nope")); c.Open(); Report("G01b wrong password rejected", false, "connected?!"); }
            catch (MySqlException ex) { Report("G01b wrong password rejected", ex.ErrorCode == MySqlErrorCode.AccessDenied, $"{(int)ex.ErrorCode}: {ex.Message}"); }
        });

        string[] samples = { "ශ්‍රී ලංකාව", "தமிழ் நாடு", "😀🎉👨‍👩‍👧", "Ünïcödé" };
        Try("G02 utf8mb4 round-trip (parameters)", () =>
        {
            using var c = new MySqlConnection(cs); c.Open();
            foreach (var s in samples)
            {
                using var ins = new MySqlCommand("INSERT INTO g_utf(s) VALUES (@s)", c);
                ins.Parameters.AddWithValue("@s", s);
                ins.ExecuteNonQuery();
            }
            using var rd = new MySqlCommand("SELECT s, HEX(s) FROM g_utf ORDER BY id", c).ExecuteReader();
            int i = 0; bool all = true; var sb = new StringBuilder();
            while (rd.Read())
            {
                var back = rd.GetString(0); var hex = rd.GetString(1);
                var expectHex = BitConverter.ToString(Encoding.UTF8.GetBytes(samples[i])).Replace("-", "");
                bool ok = back == samples[i] && hex == expectHex;
                all &= ok; sb.Append($"[{i}:{(ok ? "ok" : "MISMATCH")}]"); i++;
            }
            Report("G02 utf8mb4 round-trip (parameters)", all && i == samples.Length, sb.ToString());
        });

        Try("G03 prepared statements", () =>
        {
            using var c = new MySqlConnection(cs + ";IgnorePrepare=false"); c.Open();
            using var cmd = new MySqlCommand("SELECT CONCAT(@a, '-', @b), @b * 2", c);
            cmd.Parameters.Add("@a", MySqlDbType.VarChar);
            cmd.Parameters.Add("@b", MySqlDbType.Int32);
            cmd.Prepare();
            var before = Convert.ToInt64(Scalar(c, "SELECT VARIABLE_VALUE FROM information_schema.SESSION_STATUS WHERE VARIABLE_NAME='Com_stmt_execute'"));
            var outs = new StringBuilder();
            for (int k = 1; k <= 3; k++)
            {
                cmd.Parameters["@a"].Value = "x" + k; cmd.Parameters["@b"].Value = k;
                using var r = cmd.ExecuteReader(); r.Read(); outs.Append(r.GetString(0) + "/" + r.GetInt64(1) + " ");
            }
            var after = Convert.ToInt64(Scalar(c, "SELECT VARIABLE_VALUE FROM information_schema.SESSION_STATUS WHERE VARIABLE_NAME='Com_stmt_execute'"));
            Report("G03 prepared statements", after - before >= 3, $"results: {outs}| server Com_stmt_execute delta={after - before} (binary protocol used)");
        });

        Try("G04 multiple result sets from CALL", () =>
        {
            using var c = new MySqlConnection(cs); c.Open();
            using var r = new MySqlCommand("CALL two_sets()", c).ExecuteReader();
            int sets = 0; var sb = new StringBuilder();
            do { while (r.Read()) sb.Append(r.GetValue(0) + "=" + r.GetValue(1) + "; "); sets++; } while (r.NextResult());
            Report("G04 multiple result sets from CALL", sets >= 2, $"result sets seen={sets}: {sb}");
        });

        Try("G05 stored proc OUT params (CommandType.StoredProcedure)", () =>
        {
            using var c = new MySqlConnection(cs); c.Open();
            using var cmd = new MySqlCommand("raise_dept", c) { CommandType = CommandType.StoredProcedure };
            cmd.Parameters.AddWithValue("p_dept", "HR");
            cmd.Parameters.AddWithValue("p_pct", 0);
            cmd.Parameters.Add(new MySqlParameter("p_count", MySqlDbType.Int32) { Direction = ParameterDirection.Output });
            cmd.Parameters.Add(new MySqlParameter("p_total", MySqlDbType.Decimal) { Direction = ParameterDirection.Output });
            cmd.ExecuteNonQuery();
            Report("G05 stored proc OUT params (CommandType.StoredProcedure)", Convert.ToInt32(cmd.Parameters["p_count"].Value) >= 1,
                $"p_count={cmd.Parameters["p_count"].Value} p_total={cmd.Parameters["p_total"].Value}");
        });

        Try("G06 cancel SELECT SLEEP(30) via MySqlCommand.Cancel()", () =>
        {
            using var c = new MySqlConnection(cs); c.Open();
            using var cmd = new MySqlCommand("SELECT SLEEP(30)", c);
            var sw = Stopwatch.StartNew();
            var t = Task.Run(() => { try { return cmd.ExecuteScalar()?.ToString(); } catch (Exception ex) { return "EX " + ex.GetType().Name + ": " + ex.Message; } });
            Thread.Sleep(1500);
            cmd.Cancel();
            var res = t.Result; sw.Stop();
            // SLEEP() returns 1 when interrupted by KILL QUERY
            var alive = Scalar(c, "SELECT 'connection still usable'");
            Report("G06 cancel SELECT SLEEP(30) via MySqlCommand.Cancel()", sw.Elapsed.TotalSeconds < 10,
                $"returned after {sw.Elapsed.TotalSeconds:F1}s, result='{res}', then: {alive}");
        });

        Try("G07 cancel from another connection via KILL QUERY", () =>
        {
            using var c = new MySqlConnection(cs); c.Open();
            var id = c.ServerThread;
            using var cmd = new MySqlCommand("SELECT COUNT(*) FROM information_schema.columns a, information_schema.columns b, information_schema.columns c2 WHERE SLEEP(0) = 0 AND SLEEP(30) = 0", c);
            var sw = Stopwatch.StartNew();
            var t = Task.Run(() => { try { return cmd.ExecuteScalar()?.ToString(); } catch (Exception ex) { return "EX " + ex.GetType().Name + ": " + ex.Message; } });
            Thread.Sleep(1500);
            using (var k = new MySqlConnection(rootCs)) { k.Open(); Exec(k, $"KILL QUERY {id}"); }
            var res = t.Result; sw.Stop();
            Report("G07 cancel from another connection via KILL QUERY", sw.Elapsed.TotalSeconds < 10, $"returned after {sw.Elapsed.TotalSeconds:F1}s, result='{res}'");
        });

        Try("G08 CancellationToken on ExecuteScalarAsync", () =>
        {
            using var c = new MySqlConnection(cs); c.Open();
            using var cmd = new MySqlCommand("SELECT SLEEP(30)", c);
            using var cts = new CancellationTokenSource(1500);
            var sw = Stopwatch.StartNew(); string res;
            try { res = cmd.ExecuteScalarAsync(cts.Token).GetAwaiter().GetResult()?.ToString(); }
            catch (Exception ex) { res = "EX " + ex.GetType().Name + ": " + ex.Message; }
            Report("G08 CancellationToken on ExecuteScalarAsync", sw.Elapsed.TotalSeconds < 10, $"returned after {sw.Elapsed.TotalSeconds:F1}s, result='{res}'");
        });

        Try("G09 CommandTimeout", () =>
        {
            using var c = new MySqlConnection(cs); c.Open();
            using var cmd = new MySqlCommand("SELECT SLEEP(30)", c) { CommandTimeout = 2 };
            var sw = Stopwatch.StartNew(); string res;
            try { res = cmd.ExecuteScalar()?.ToString(); } catch (Exception ex) { res = "EX " + ex.GetType().Name + ": " + ex.Message; }
            Report("G09 CommandTimeout", sw.Elapsed.TotalSeconds < 10, $"returned after {sw.Elapsed.TotalSeconds:F1}s, result='{res}'");
        });

        Try("G10 multi-statement batch + transaction", () =>
        {
            using var c = new MySqlConnection(cs); c.Open();
            using var tx = c.BeginTransaction();
            new MySqlCommand("INSERT INTO g_utf(s) VALUES ('tx1'); INSERT INTO g_utf(s) VALUES ('tx2');", c, tx).ExecuteNonQuery();
            tx.Rollback();
            var n = Convert.ToInt32(Scalar(c, "SELECT COUNT(*) FROM g_utf WHERE s LIKE 'tx%'"));
            Report("G10 multi-statement batch + transaction", n == 0, $"rows after rollback={n}");
        });

        Try("G11 GetSchema / column metadata", () =>
        {
            using var c = new MySqlConnection(cs); c.Open();
            using var r = new MySqlCommand("SELECT * FROM emp LIMIT 1", c).ExecuteReader();
            var cols = r.GetColumnSchema().Select(x => $"{x.ColumnName}:{x.DataTypeName}{(x.IsKey == true ? "(PK)" : "")}{(x.IsAutoIncrement == true ? "(AI)" : "")}");
            Report("G11 GetSchema / column metadata", true, string.Join(", ", cols));
        });

        Try("G12 JSON column type mapping", () =>
        {
            using var c = new MySqlConnection(cs); c.Open();
            using var r = new MySqlCommand("SELECT j FROM doc LIMIT 1", c).ExecuteReader();
            r.Read();
            Report("G12 JSON column type mapping", true, $"DataTypeName={r.GetDataTypeName(0)} .NET={r.GetFieldType(0).Name} value={r.GetValue(0)}");
        });

        Console.WriteLine($"G99\tEND\tfails={fails}");
        return fails;
    }

    static void Exec(MySqlConnection c, string sql) { using var cmd = new MySqlCommand(sql, c); cmd.ExecuteNonQuery(); }
    static object Scalar(MySqlConnection c, string sql) { using var cmd = new MySqlCommand(sql, c); return cmd.ExecuteScalar(); }
}
