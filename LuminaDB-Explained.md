# LuminaDB, explained in simple words

This guide explains the whole LuminaDB project from start to finish:

- the problem
- the ideas
- the tools it is built with
- how it was built and tested
- why you can trust it
- what every part does
- what you can do with it

It is written so that you can learn from it and explain it to anyone, even someone who has never written code.

---

## 1. The problem

To build a website with PHP and a MySQL database on Windows, most people install **three separate programs**:

| Program | What it is for |
|---|---|
| **XAMPP** | Runs Apache (the web server), PHP and MySQL/MariaDB on your PC |
| **MySQL Workbench** | A window for writing SQL, designing tables and drawing diagrams |
| **phpMyAdmin** | A web page for looking at and changing your databases |

Together they work, but they often go wrong, especially for beginners:

- **"Port 3306 is in use"** or **"Apache won't start, port 80 is busy".** Another program, often Skype or another MySQL, is already using the same door.
- **"MySQL shutdown unexpectedly".** XAMPP's most famous error. People delete their data folder trying to fix it and lose everything.
- **"VCRUNTIME140.dll is missing".** A Microsoft file the programs need is not on the PC.
- **Collation errors** such as `Unknown collation utf8mb4_0900_ai_ci`. A file exported from MySQL 8 or Workbench will not import into XAMPP.
- **"Access denied for user root".** Passwords and accounts get confusing.
- **Lost work.** One `DELETE FROM students;` without a `WHERE` and every row is gone, with no undo.
- **Error messages nobody understands,** like `Error 1064: You have an error in your SQL syntax near ''`.
- **Three programs that don't know about each other.** Workbench doesn't know XAMPP's port, phpMyAdmin needs its own setup, and so on.
- **Old PCs.** Many labs and homes still have Windows 7 or 32-bit Windows, which new versions don't support.

**The goal:** one Windows app that does the job of all three, starts with one click, never leaves you stuck, explains every error in plain words, and protects your data.

---

## 2. The big idea

> **LuminaDB = XAMPP + Workbench + phpMyAdmin in one app, and it never leaves you stuck.**

The idea rests on five promises.

1. **It just starts.** If a port is busy, it picks another one and tells you. No admin rights, no Windows service, no extra downloads.
2. **It explains.** Every error, from SQL, PHP or the server, comes with a plain-English reason and, where safe, a one-click fix.
3. **It protects your data.** Risky commands ask first, an Undo copy is taken before changes, and automatic backups run all the time.
4. **It runs on old PCs too.** Windows 7, 8, 10, 11, in 64-bit or 32-bit.
5. **Everything stays on your PC.** It listens only on `127.0.0.1` (your own computer), so nobody on the network can reach your database by accident.

### Other ideas that came up and were built

- **The "Ledger" look.** The app looks like a paper notebook: ruled lines, a red margin line, ink-stamped buttons, binder tabs on the side. It is calm and friendly, not a grey technical tool. Mistakes appear as **margin notes** beside the line, like a teacher's red pen.
- **The Doctor.** A page that checks everything that usually breaks (port, antivirus, crashed tables, wrong settings, disk space) and offers a fix button.
- **Safe updates and Undo.** Before a command changes data, LuminaDB quietly copies the rows it will touch, so you can press **Undo change**.
- **The import rewriter.** A MySQL 8 file is fixed automatically while it is imported, and every change is listed first.
- **The mail catcher.** Emails sent by your PHP code land inside LuminaDB instead of going to real people. Good for testing "forgot password" pages.
- **Project addresses.** Each project gets its own address, like `http://myapp.localhost`.
- **Three engine sets.** Different versions of MariaDB, PHP and Apache for new PCs, old PCs and 32-bit PCs, chosen automatically.

---

## 3. The tools it is built with (tech stack) and why

| Part | What was used | Why this choice |
|---|---|---|
| The app itself | **C#** on **.NET Framework 4.8**, with **WPF** for the windows | .NET 4.8 is built into Windows 10/11 and can be installed on Windows 7. WPF draws custom designs (the paper look) smoothly. "AnyCPU" means one exe runs on both 64-bit and 32-bit Windows. |
| Database | **MariaDB** 11.8 (new PCs), 10.4.34 (Windows 7), 10.4.22 (32-bit) | Free, open source and works like MySQL. 11.8 is the current long-term version; 10.4 is the newest that still runs on Windows 7. |
| PHP | **PHP 8.5** (new PCs), **PHP 8.2** (Windows 7 / 32-bit) | Runs your PHP websites. 8.2 is the newest that runs on Windows 7. |
| Web server | **Caddy** in front, **Apache** only when needed | Caddy is a single small, fast program. Apache starts only for projects with a `.htaccess` file (Laravel, WordPress pretty links), just like XAMPP would. |
| PHP runner | **php-cgi** with worker processes (FastCGI) | Several pages can load at the same time. `php -S` was rejected because one slow page blocks all the others. |
| SQL editor | **AvalonEdit** | A proven text-editor component for WPF, with colouring and underlines. |
| Talking to MariaDB | **MySqlConnector** (.NET library) | Fast and reliable, and can cancel a running query. |
| Extra tools inside | **phpMyAdmin 5.2.3**, **Composer**, a WordPress zip | For people who still want phpMyAdmin, or need Laravel or WordPress. |
| Installer | **Inno Setup** | Builds a normal Windows "Setup.exe". Installs for one user, without admin. |
| Fonts | Bricolage Grotesque, IBM Plex Sans/Mono | Embedded in the exe, so no internet is needed. |
| Licence | **MIT** (open source) | Anyone can use, read and change it for free. |

### How the pieces fit together

```
                 ┌─────────────────────────────┐
   You  ───────▶ │   LuminaDB.exe (the window) │
                 └──────────────┬──────────────┘
         starts, watches and stops all of these:
     ┌─────────────┬────────────┼─────────────┬──────────────┐
     ▼             ▼            ▼             ▼              ▼
  MariaDB       Caddy  ──▶   php-cgi       Apache        Mail catcher
  (database,   (web server,  (runs PHP)   (only for      (catches emails
   port 3306)   port 80)                   .htaccess)     PHP sends, 2525)
```

Everything listens on `127.0.0.1` only, which means "this PC only".

---

## 4. How it was built, step by step

### Step 0: prove it can work, before writing the app

Nothing was built on guesses. Two test projects came first.

**EngineSpike** tested MariaDB 10.4.12, 10.4.34 and 11.8 with no installer, no admin and no service. It showed that:

- When port 3306 was busy, it moved to 3307.
- After a simulated **power cut** (a hard kill), all 1500 committed rows were still there and `CHECK TABLE` said OK. So the famous "MySQL shutdown unexpectedly" scare is mostly the database protecting itself, and it can be handled calmly.
- The Microsoft runtime files ship inside MariaDB, so "VCRUNTIME140.dll missing" can't happen.
- MariaDB 11.8 can't run on Windows 7: its files use Windows 8 functions. That is why old PCs get 10.4.

**ParityTests** asked: *can this really replace XAMPP + Workbench + phpMyAdmin?* It tested:

- Every common SQL feature: joins, views, procedures, triggers, events, users, Sinhala/Tamil/emoji text.
- Workbench and phpMyAdmin export files, and MySQL 8 syntax. This produced the list of 24 automatic fixes, R01–R24.
- PHP with a sample CRUD site (27 of 27 checks), phpMyAdmin (9 of 9), Caddy and Apache under 20–100 requests at once (0 failures).
- The traps to avoid, for example:
  - 10.4 makes `latin1` tables unless told otherwise.
  - Caddy opens to the network unless told `bind 127.0.0.1`.
  - php-cgi quits after 500 requests unless `PHP_FCGI_CHILDREN` is set.

All of this went into **SPEC.md**, the plan for the whole app.

### Steps 1 to 11: building the app

| Step | What was built |
|---|---|
| 1 | **Engine services:** start, stop, watch MariaDB, choose free ports, store the root password encrypted, recover after a crash |
| 2 | **The window (shell):** the paper design, 12 binder tabs (F1–F12), the Today page, the status line |
| 3 | **Notebook:** SQL editor with live mistake checking, explanations, safety questions, Undo |
| 4 | **Tables:** browse and edit rows, design tables, operations, views/procedures/triggers |
| 5 | **Projects and the web stack:** PHP projects, Caddy/Apache, the PHP error page, mail catcher |
| 6 | **Doctor, backups, snapshots** |
| 7 | **Import and export:** .sql (with the MySQL 8 rewriter), CSV, Excel, JSON |
| 8 | **Diagram:** draw tables and relations, sync them to the database, export PNG/SVG/PDF |
| 9 | **Users, Server, Settings, phpMyAdmin switch** |
| 10 | **Packaging:** the two installers |
| 11 | **Full test pass,** VirusTotal scan |

After the steps came polish:

- **Accessibility:** colour contrast, text size, keyboard rings, screen-reader names.
- **Speed and memory:** memory went from about 504 MB to 244–355 MB, and switching pages went from 100–360 ms to 8–25 ms.
- **The rest:** the icon, the tray icon, quick tips, error reports and command-line tools.

### Bugs found while filming the walkthrough video (and fixed)

Making the demo video was also a real-world test. It found four real bugs.

1. **Picking a linked row left the box empty.** In Tables › New row, choosing "2 · Bread" from the product list showed nothing, because refreshing the list wiped the text. Fixed: changes are ignored while the list refills.
2. **PHP refused to start ("ASLR" error)** when another copy of the same PHP was running. Fixed: LuminaDB gives PHP's cache (OPcache) its own name and a fallback file cache.
3. **A stray warning on the PHP error page,** caused by a PHP 8.5 change. Fixed.
4. **Every page ran twice.** The browser also asks for `/favicon.ico`; when a project had none, the request went to `index.php`, so the code ran twice (two rows saved, two emails sent). Fixed: a missing favicon now gets "404 not found", like XAMPP's Apache.

---

## 5. How it was tested

| Kind of test | What it means | Result |
|---|---|---|
| **Automatic tests** (`dotnet test`) | Small programs that check the code gives the right answer. Many run the **real** MariaDB, PHP, Caddy and Apache, in folders with spaces in their names. | **400 of 400 pass** |
| **Both engine sets** | The database tests run on MariaDB 11.8 *and* 10.4 | Pass |
| **Release tests** | The three engine sets *exactly as shipped* are started, a dump is imported, and a PHP site reads the database. The 32-bit set runs as real 32-bit programs. | Pass |
| **Binary check** (`check-imports.ps1`) | Reads every program file to make sure the Windows 7 sets use nothing newer than Windows 7 | Clean |
| **Using the real app** | Each step was clicked through in the real window (by automation), at 1366×768 laptop size and smaller | Bugs found → fixed |
| **Layout audit** | A built-in check that lists anything cut off on screen, run over 22 page states | Nothing cut off |
| **Installer tests** | Install, upgrade while running, uninstall, reinstall (old data found again), refusal on the wrong PC | Pass |
| **VirusTotal** | Over 60 antivirus engines scanned both installers | **0 flagged** |
| **Walkthrough video** | The real installer and app used end to end for 5.4 minutes | Found 4 bugs, all fixed |

### What is honestly *not* tested yet

- A **real Windows 7 PC** and a **real 32-bit PC**. These were checked by reading the files and running the sets on Windows 10.
- A PC **without .NET 4.8** (the installer downloads it from Microsoft).
- The tray icon's right-click menu and balloon, by hand.
- The "delete my data too" choice when uninstalling.
- **Code signing.** Windows may show "unknown publisher" until the installer is signed.
- The rebuilt installers should get a fresh VirusTotal scan before release.

---

## 6. Why you can trust it

- **Nothing reaches the network by accident.** MariaDB, the web server and the mail catcher listen on `127.0.0.1` only. Demo mode, for showing a site on a phone, is the one exception: you switch it on, and it turns itself off after the minutes you choose.
- **No spying.** There is no telemetry, no account, no ads. Error reports are **never sent by themselves**: you read the report first, and your PC name, user name, passwords, emails and SQL values are removed from it.
- **Your data is protected in layers.**
  1. Risky commands (DELETE without WHERE, DROP, TRUNCATE) **ask first** and show how many rows they will affect.
  2. An **Undo copy** is taken before a change.
  3. A **backup** is taken before risky commands, every 30 minutes for databases that changed, and when you close.
  4. A **restore** backs up what is there first, so even a restore can be undone.
  5. A data folder that went wrong is **moved aside, never deleted**.
- **Crashes are handled calmly.** A power cut was tested: committed data survived. Closing the app always stops MariaDB cleanly.
- **Your passwords stay safe.** The root password is stored encrypted with Windows DPAPI, so it only opens on your account.
- **It needs no admin rights.** It installs only for you, in `%LOCALAPPDATA%\LuminaDB`, and changes nothing system-wide.
- **It is open source (MIT).** Anyone can read every line of the code.
- **It doesn't touch other copies.** A second LuminaDB, or your own XAMPP, is never stopped. It only manages processes started from its own folder.
- **It is honest about limits.** What isn't verified is written down (see above), not hidden.

---

## 7. Every feature, page by page

The window has 12 binder tabs on the side. Each one opens with its F-key.

| Key | Page | What it does |
|---|---|---|
| F1 | **Today** | The home page. Shows whether the database and web server are running (and on which port), your databases, connection details to copy for PHP, quick health checks and the server's messages. |
| F2 | **Projects** | Your PHP websites (details below). |
| F3 | **Notebook** | Write and run SQL, with help (details below). |
| F4 | **Tables** | See and change data without writing SQL (details below). |
| F5 | **Diagram** | Draws your tables and how they link (an ER diagram). You can also draw new tables, then press Run to create them. Export as PNG, SVG, PDF or a CREATE script. |
| F6 | **Import** | Bring in `.sql`, `.sql.gz`, `.zip`, CSV or Excel files. MySQL 8/Workbench files are fixed on the way in. Export to .sql (optionally XAMPP-friendly), CSV, JSON or Excel. |
| F7 | **Users** | Create database users, choose what they can do with plain checkboxes (or presets like "read only"), change passwords, lock accounts, roles. |
| F8 | **Server** | Live status (uptime, queries per second, memory), who is connected (stop a query), all 658 server settings with explanations, every log, and a built-in MariaDB console. |
| F9 | **Doctor** | Checks everything that usually breaks and offers fixes. A red dot on the tab means something needs attention. |
| F10 | **Backups** | Every backup of every database, even deleted ones. Restore, back up now, see the schedule. |
| F11 | **Mail** | Emails your PHP code sent, caught safely. View them as HTML, text or source, and save attachments. |
| F12 | **Settings** | Ports, start with Windows, the tray, the root password, backup timing, text size (100/110/125%), the phpMyAdmin switch, moving the data folder, command-line tools, About and Report a problem. |

### Notebook (the SQL editor) in more detail

- **Live checking.** While you type, mistakes get a wavy underline and a **margin note** with a **Fix** button. For example: a misspelled keyword ("did you mean SELECT?"), a table that doesn't exist, or a missing comma.
- **Verdict bar.** Says OK, CAREFUL, FIX or NOT YET, plus a plain-English sentence of what the command will do ("Deletes the rows of marks where…").
- **NOT YET** means you are running a line that needs a table created higher up that hasn't run yet. It offers "Run from line N to here".
- **Errors explained.** For example, "Duplicate entry '5' for key PRIMARY" becomes *"That id is already used"*, with Show me (jumps to the line) and Fix.
- **Safety questions.** For example: `DELETE FROM marks` without WHERE → *"This deletes all 6 rows. Add a WHERE for me / Run anyway / Cancel"*.
- **Undo change** puts the data back.
- **Snippets.** Type `sel` and press Tab to get a whole SELECT with blanks to fill in. There are 19 built in, and you can save your own.
- **Completion.** It suggests table and column names while you type.
- **Tidy up** formats your SQL neatly.
- **Sheets.** Several tabs of SQL, with Open/Save `.sql` files and auto-save.
- **Results.** A fast grid; save as CSV, Excel or JSON.
- **Focus mode** (Ctrl+Shift+M) gives the editor the whole window.

### Tables in more detail

- **Rows:** page through rows, search every column, filter, sort. Double-click to edit. The SQL it will run is shown underneath.
- **Row form:** the right box for each column (date picker, yes/no, list). A **linked column** (foreign key) offers the rows it points at, like "2 · Bread".
- **Structure:** columns, keys, indexes, foreign keys, checks and triggers, edited in a grid. It shows the ALTER it will run.
- **Operations:** rename, copy, move, empty, drop, change collation, check or repair.
- **Definition:** edit views, procedures, functions, triggers and events.

### Projects in more detail

- **New project:** pick a starter: Empty, CRUD (list/add/edit/delete), Login and registration, REST API, Laravel or WordPress. LuminaDB makes the folder, a database with the same name and `config.php`, and starts the web server.
- **Addresses:** `http://myapp.localhost` and `http://localhost/myapp/`.
- **Import an old XAMPP folder.** LuminaDB reads your PHP to find the database name, and offers to import the `.sql` file in the folder.
- **The PHP error page.** When your PHP breaks, you get a clear page: the file, the line, the code around it and a plain explanation (about 30 common mistakes, such as the old `mysql_` functions, an undefined variable or a missing semicolon).
- **Logs:** PHP errors and requests, live.
- **PHP settings per project:** upload size, memory, time limit, time zone.
- **Composer** buttons for install and update.
- **Snapshots:** save code and database together, and restore later.
- **Demo:** show the site on a phone on the same Wi-Fi, with a QR code, for a limited time.
- **Submission pack:** one zip with the code, the database `.sql` and a README, ready to hand in or send.
- **Open in VS Code / Notepad++ / Explorer** with one click.

### Extras

- **Tray icon by the clock.** Closing the window keeps LuminaDB running, like XAMPP. The dot colour shows the state: green running, amber busy, red stopped by itself, grey stopped. Right-click to start, stop, open phpMyAdmin or quit.
- **Quick tips:** one helpful line on each page (45 tips).
- **Command-line tools.** `php`, `composer`, `mariadb`/`mysql`, `mariadb-dump`/`mysqldump` in `LuminaDB\bin`, always pointing at the right port. Settings can add them to every terminal.
- **phpMyAdmin**, off by default, at `http://phpmyadmin.localhost`.
- **Report a problem:** makes a cleaned report you can read, copy or save.
- **Opens `.sql`, `.csv` and `.xlsx` files** with "Open with LuminaDB".

---

## 8. The installers

| File | For | Size |
|---|---|---|
| `LuminaDB-Setup.exe` | Windows 10/11, 64-bit | about 94 MB |
| `LuminaDB-Setup-Win7-32bit.exe` | Windows 7/8/8.1 (64-bit), and any 32-bit Windows | about 108 MB |

- Installs for you only, with no admin rights, into `%LOCALAPPDATA%\LuminaDB`.
- Running the wrong file on a PC gives a clear message naming the right one.
- Upgrading closes the running copy cleanly first. Uninstalling keeps your data unless you say otherwise.
- Everything is inside: MariaDB, PHP, Caddy, Apache, phpMyAdmin, Composer, the WordPress zip. Nothing else needs downloading, except .NET 4.8 on very old PCs.

---

## 9. How the walkthrough video was made

The 5.4-minute video (`walkthrough\LuminaDB-walkthrough.mp4`) was made by a script, not by hand.

1. **A driver script** (`drive.ps1`, PowerShell) moved the real mouse, clicked the real buttons (found by name through Windows UI Automation) and typed with natural speed. It did this on the **real installer and app**, with nobody touching the PC.
2. **Recording:** `ffmpeg` recorded the screen. Every frame got an exact clock time, and every click, key press and caption was written to a log with the same clock. So the click effects land exactly where the pointer was.
3. **Six scenes:**
   1. Install
   2. Notebook
   3. Tables and Diagram
   4. PHP projects and Mail
   5. Import, Export and Users
   6. Backups, Server, Doctor and Settings
4. **Editing by script** (`make-video.ps1`):
   - Quiet stretches play 4× faster, and the installer 8× faster.
   - Each caption gets enough time to read (based on its word count).
   - Click ripples and key-press labels are drawn at the logged positions.
   - Private parts of the screen are blurred.
5. **Sounds:** click and typing sounds were generated from maths formulas (no downloaded sound files) and placed at the logged times.
6. **A reset script** put the PC back to a clean state between takes, so every take started the same way.

---

## 10. What you can do with LuminaDB

- **Learn SQL.** The Notebook catches mistakes as you type and explains every error, so you learn faster and stay out of trouble.
- **Build PHP websites:** a shop, a blog, a booking system, a login page. Each project gets its own address and database in one click.
- **Use Laravel or WordPress** without setting up Apache or MySQL yourself.
- **Design a database visually** in the Diagram, then create it with one click, and export the picture for a report.
- **Open files from other tools:** Workbench, phpMyAdmin and MySQL 8 dumps are fixed automatically.
- **Work with Excel data.** Import a spreadsheet into a table, or export a table to Excel.
- **Test emails safely.** Signup and "forgot password" emails land in Mail, not in real inboxes.
- **Show your site on a phone** with Demo mode and a QR code.
- **Hand in or share a project** as one tidy zip (code + database + README).
- **Replace XAMPP** on an old Windows 7 or 32-bit PC.
- **Manage users and permissions** for a real application.
- **Run it all year:** backups, the Doctor and Undo keep your work safe.

---

## 11. Small words, explained (glossary)

| Word | Simple meaning |
|---|---|
| **Database** | An organised place to keep data, like a set of Excel sheets that programs can read and write. |
| **Table** | One "sheet" in a database: rows (records) and columns (fields). |
| **SQL** | The language for talking to a database: `SELECT`, `INSERT`, `UPDATE`, `DELETE`… |
| **MariaDB / MySQL** | Database programs. MariaDB is a free twin of MySQL; they speak the same SQL. |
| **Server** | A program that waits for requests and answers them, here on your own PC. |
| **Port** | A numbered "door" on your PC that a server listens at (3306 for the database, 80 for websites). Two programs can't use the same door. |
| **127.0.0.1 / localhost** | "This computer". Only your own PC can reach it. |
| **PHP** | A language for making web pages that read and write the database. |
| **Web server** (Caddy, Apache) | The program that sends web pages to your browser. |
| **Foreign key** | A column that points at a row in another table (an order points at a product). |
| **Primary key** | The column that uniquely identifies each row, usually `id`. |
| **Collation** | The rules for comparing and sorting text. Different MySQL versions name them differently, which causes import errors. |
| **utf8mb4** | A text encoding that stores every language and emoji, including Sinhala and Tamil. |
| **Backup / restore** | Save a copy / put the copy back. |
| **Dump (.sql file)** | A database saved as a text file full of SQL commands that rebuild it. |
| **ER diagram** | A picture of tables as boxes and their links as lines. |
| **.htaccess** | A settings file for Apache, used by Laravel and WordPress for tidy addresses. |
| **Composer** | The tool PHP projects use to download libraries. |
| **Installer** | The Setup.exe that copies the app onto a PC. |
| **Open source (MIT)** | The code is public; anyone can use and change it for free. |
| **Telemetry** | An app sending usage data home. LuminaDB has none. |
| **VirusTotal** | A website that scans a file with over 60 antivirus programs at once. |
| **Unit / integration test** | A small program that checks another piece of code works, alone (unit) or with the real database (integration). |

---

## 12. LuminaDB in one minute (to explain to anyone)

> Making a website on Windows usually means installing XAMPP, Workbench and phpMyAdmin, three programs that often break with confusing errors like "port in use" or "MySQL shutdown unexpectedly", and one wrong command can delete all your data.
>
> **LuminaDB is one free app that replaces all three.** You install it without admin rights. It starts the database and web server by itself, picking a free port if one is busy. When you write SQL or PHP, it points at mistakes as you type and explains errors in plain English. Before anything risky, it asks first and keeps an Undo copy and automatic backups. It can make a PHP project, even Laravel or WordPress, in one click, catch the emails your code sends, draw your database as a diagram, and import files from MySQL 8 or Excel.
>
> It runs on Windows 7 to 11, in 32-bit or 64-bit. It stays on your own PC and sends nothing anywhere. It was tested with 400 automatic tests, on the real engines, and scanned clean by over 60 antivirus engines. It is open source under the MIT licence, made by IzyaanTech.
