using System.Collections.Generic;

namespace LuminaDB.App.ViewModels
{
    /// <summary>
    /// The quick tips: one line at the bottom of each page, the next one on each visit or with "Next tip". Each names a
    /// real button or key on that page. Never a pop-up; Settings › Quick tips turns them off.
    /// </summary>
    public static class Tips
    {
        public static readonly IReadOnlyDictionary<string, string[]> ByPage = new Dictionary<string, string[]>
        {
            ["today"] = new[]
            {
                "F1 to F12 open the tabs on the right: F3 is the Notebook, F4 Tables, F10 Backups.",
                "Copy PHP connection line puts a ready mysqli line on the clipboard, with the right port and password.",
                "Start everything starts the database and the web server together.",
                "Closing the window keeps LuminaDB running by the clock. Right-click its icon › Quit LuminaDB to stop everything.",
                "Ctrl+R reads the page again: databases, sizes and the rest.",
            },
            ["projects"] = new[]
            {
                "New project makes the folder, a database and config.php, ready at http://name.localhost.",
                "Import a folder copies a project from XAMPP's htdocs, or anywhere else, and sets it up.",
                "Take snapshot keeps the code and the database together; Restore puts both back.",
                "Start demo shows a project on your Wi-Fi, with a QR code for a phone or another laptop.",
                "Build the pack makes one zip with the code and the database, ready to hand in or share.",
                "A PHP error opens on a plain page with the line that failed and what it means.",
            },
            ["notebook"] = new[]
            {
                "Ctrl+Enter runs the command under the cursor; Ctrl+Shift+Enter runs them all, top to bottom.",
                "Type sel and press Tab for a whole SELECT. Ctrl+Space lists the tables and columns.",
                "Safe updates ask first before a DELETE or UPDATE without WHERE, and Undo can take it back.",
                "Ctrl+Shift+M gives the editor the whole window. Esc brings everything back.",
                "Examples has ready-made SQL, and the usual mistakes with what fixes them.",
                "Save the result as CSV, Excel or JSON from the bar above the rows.",
                "Ctrl+T opens a new sheet; Ctrl+S saves it as a .sql file of your own.",
            },
            ["tables"] = new[]
            {
                "Double-click a row to edit it. Delete removes the selected rows, and Undo keeps a copy.",
                "A column that points at another table offers a list of the rows it can point to.",
                "Ctrl+F jumps to the search box; filters narrow the rows without writing SQL.",
                "Design a new table with columns, keys and indexes; Copy SQL shows what will run.",
            },
            ["diagram"] = new[]
            {
                "Draw tables and relations, then Review script shows the SQL before anything changes.",
                "Export PNG for Word or PowerPoint, PDF to print, SVG to stay sharp at any size.",
                "Arrange lays the tables out again: parents on the left. Ctrl+wheel zooms.",
            },
            ["import"] = new[]
            {
                "Drop a .sql, .csv or .xlsx file anywhere on this page to bring it in.",
                "Dumps from MySQL 8 (utf8mb4_0900_ai_ci and friends) are fixed as they come in.",
                "Export can make a .sql file that also opens in XAMPP, MySQL 8 and phpMyAdmin.",
                "In Explorer, right-click a .sql, .csv or .xlsx file › Open with › LuminaDB.",
            },
            ["users"] = new[]
            {
                "Give each project its own account, with read and write on just its own database.",
                "Root password… sets or changes it on every root account at once.",
            },
            ["server"] = new[]
            {
                "Variables explain each setting. Search for words like packet, log or timeout.",
                "Console is the mariadb command line, already connected as root.",
                "Connections shows who is connected; Disconnect ends a stuck one.",
            },
            ["doctor"] = new[]
            {
                "The Doctor checks ports, antivirus, crashes and broken settings. Every fix takes a backup first.",
                "A red dot on the Doctor tab means something here needs a look.",
            },
            ["backups"] = new[]
            {
                "Databases that changed are backed up every 30 minutes, before risky commands and when LuminaDB closes.",
                "Restore backs up what's there now first, so a restore can be taken back too.",
                "Back up everything now before a big change.",
            },
            ["mail"] = new[]
            {
                "Emails your PHP code sends land here. Nothing leaves the PC.",
                "Copy SMTP settings gives PHPMailer or Laravel the right host and port.",
            },
            ["settings"] = new[]
            {
                "Text size draws everything larger, for a projector or a high-resolution screen.",
                "When I close the window: keep LuminaDB running in the tray, or quit.",
            },
        };

        public static int Count
        {
            get
            {
                int n = 0;
                foreach (var t in ByPage.Values) n += t.Length;
                return n;
            }
        }
    }
}
