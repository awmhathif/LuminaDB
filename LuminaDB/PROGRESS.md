# LuminaDB build progress

Spec: `..\SPEC.md`. Build order: SPEC §11.

## Step 1: Core engine services (DONE, 2026-11-23)

`src\LuminaDB.Core` (net48, AnyCPU):

| File | What it does |
|---|---|
| `AppPaths.cs` | Every folder under one root (portable layout). One data folder per engine set. |
| `WindowsInfo.cs` | Real Windows version from the registry (not the lying `OSVersion`), 32/64-bit, RAM. |
| `EngineSet.cs` | Modern / Legacy / X86 engine set; exe names probed (mariadbd vs mysqld); version read from the exe. `EngineSetSelector` picks the set for the PC. |
| `PortManager.cs` | Free-port picking: last used port first, then preferred, then next free. Busy = a listener on ANY address (0.0.0.0, ::, 127.0.0.1) or failed exclusive bind. |
| `MariaDbConfigWriter.cs` | Writes the verified my.ini (ParityTests/database/RESULTS.md §3) fresh at each start; `character_set_collations` only on 11.2+; buffer pool = RAM/8, clamped 64–256 MB. |
| `ManagedProcess.cs` | Child process with no window, output to a rotating log and events; adopt by PID; find leftovers by exe path. |
| `MariaDbService.cs` | Init data folder (+ engine stamp), start, wait ready, port-race retry, clean SHUTDOWN with hard-kill fallback, crash detection, adopt a running server after the UI is reopened, root password on all root accounts. |
| `RootCredentialStore.cs` | Root password encrypted with DPAPI; unreadable on another PC → `NeedsPassword`, `UnlockAsync`. |
| `JsonFile.cs` | Atomic JSON state/PID/stamp files; a corrupt file counts as missing. |

Tests: `dotnet test tests\LuminaDB.Core.Tests`. There are 55, all passing. They include 15 integration tests that run the real MariaDB 11.8.10 and 10.4.34 from `..\EngineSpike\engines` (override the location with the `LUMINA_ENGINES` env var), in temp folders whose paths contain a space. They cover:
- the verified settings (utf8mb4 / strict mode / local_infile / lower_case / event scheduler / 256M packet) and 127.0.0.1-only listening
- Sinhala text and emoji round-trips
- data surviving a restart
- a busy port (held on 0.0.0.0) being skipped and remembered
- adopting a running server (with and without the PID file)
- a crash being reported with no committed data lost and CHECK TABLE returning OK
- the root password on every root account, stored encrypted and used for SHUTDOWN, with lost-secret unlock
- a half-made data folder being moved aside rather than deleted
- a data folder made by another engine set being refused

## Step 2: Shell UI (DONE, 2026-11-23)

`src\LuminaDB.App` (WPF, net48, AnyCPU, `asInvoker` manifest declaring Win7–11, builds `LuminaDB.exe`). Screenshot: `docs\step2-today.png`.

- **Ledger theme** (`Theme\Ledger.xaml`, `Icons.xaml`):
  - paper, ink, blue and vermillion tokens
  - "ink-stamped" buttons built from a plain offset rectangle (no effects, so it stays fast on old GPUs)
  - torn-paper edge, thin scrollbars
  - our own stroke icons
- **Fonts** are embedded in the exe, so nothing is needed from the internet: Bricolage Grotesque and IBM Plex Sans/Mono (Instrument Serif was removed once notes moved to Plex Sans). They are static TTFs, and the OFL licences are copied to `licenses\`.
- **Masthead** status sentence covers running on :port, starting, stopping, stopped safely, stopped unexpectedly, couldn't start (with a "Why?" slip showing the log tail), and engine missing.
  - The note names the program holding 3306 (`PortOwner`, via GetExtendedTcpTable).
- **Activity line:** real queries per second from `information_schema.GLOBAL_STATUS`.
- **12 binder tabs** (F1–F12, F10 handled as a system key). They share one font size computed from the window height, so all twelve fit a 1366×768 lab screen.
  - Doctor tab shows a red dot when there are issues.
  - Sections not built yet show what they'll do and which build step delivers them.
- **Today:**
  - Real database list from information_schema; New database slip with live name validation, utf8mb4.
  - Connection sheet with Copy PHP `mysqli` line and Copy URL.
  - Doctor's report built from `StartupChecks`: engine, port, 127.0.0.1-only, folder path (OneDrive / non-ASCII / network / long), disk, writable.
  - Receipt tape of server output with timestamp noise removed.
  - The receipt tape moves below the other columns under 1120 px wide; the masthead shrinks under 1150 px.
- **Behaviour:**
  - Server starts when the app opens, and closing the window always does a clean SHUTDOWN.
  - When Windows logs off it does a bounded stop.
  - One window per LuminaDB folder (mutex); a second launch brings the first window forward.
  - Unexpected errors go to `logs\app.log` with a calm message.
  - Root-password slip appears when the stored secret can't be read on this PC.
- **Checked by hand** against real MariaDB 11.8 using UI Automation and window screenshots (1280×800, 1366×768, 1000×640):
  - first start
  - create a database through the slip (a bad name is explained and Create stays disabled)
  - port 3306 held by another program
  - crash (killed the app's own server by PID) followed by Start again, with data intact
  - closing the window stops MariaDB with exit code 0
- **Tests:** 83, all passing. New ones cover the database-name rules, size/date formatting, `PortOwner` (IPv4 and IPv6), path problems, the port check, and an integration test per engine for list/create/activity/checks.

Dev run (uses the spike engines and web bundle, data in `.devroot\`, which git ignores): double-click `run-dev.cmd`, or `.\run-dev.ps1` (add `-Build` to rebuild first; close LuminaDB before building). Starting `LuminaDB.exe` directly uses the bin folder as the app folder and says MariaDB is missing.

## Step 3: Notebook (DONE, 2026-11-23)

Screenshot: `docs\step3-notebook.png`.

**Core SQL** (`src\LuminaDB.Core\Sql\`). The checks need no server and are unit-tested:

| File | What it does |
|---|---|
| `SqlLexer.cs`, `SqlSplitter.cs` | Tokens and statements, including `DELIMITER`, `/*! */` version comments, and quotes and comments that are never closed. |
| `SqlKeywords.cs` | MariaDB word lists, plus closest-word suggestions (OSA distance). |
| `SqlFormatter.cs` | Tidy up: one clause per line and keywords in capitals. Routines and the gaps between statements are kept as written. |
| `Schema.cs` | Snapshot of databases, tables and columns (with friendly types), read from information_schema. |
| `StatementInfo.cs` | What a command reads, changes, creates or drops. Used by every check below. |
| `SqlGuard.cs` | Margin corrections with one-click fixes, plus a plain-English summary of the command. |
| `ErrorExplainer.cs` | Server errors (1054, 1062, 1064 with the "most likely" spot, 1146, 1452, 1366, connection loss…) explained, with a fix where one is safe. |
| `SafetyGate.cs` | Asks before DELETE or UPDATE without WHERE, DROP, TRUNCATE and DROP COLUMN, giving an exact row count. "Add a WHERE for me" is offered. |
| `UndoService.cs` | Copies what a command will change into the hidden `lumina_undo` schema, which also covers DROP TABLE, DROP DATABASE and RENAME. Keeps 20 batches. Tables over 200k rows or 64 MB are skipped and the user is told so. |
| `NotebookSession.cs` | One connection that keeps USE and @variables between commands. Reconnects after a server restart. Stop is done with KILL QUERY. Results are capped at 100k rows. |

**App:**
- **Editor:** AvalonEdit on ruled paper with a red margin line. SQL colours come from the same word lists the checker uses.
- **Mistakes shown live:**
  - Wavy underlines appear 260 ms after typing stops.
  - Margin notes sit beside their line, never overlap, and carry a Fix button.
  - The verdict bar shows OK / CAREFUL / FIX plus the plain-English summary.
- **Toolbar:**
  - Run (Ctrl+Enter) runs the command under the cursor or the selection.
  - Run all (Ctrl+Shift+Enter) makes one Undo copy for the whole run.
  - Stop takes Run's place while a command runs.
  - The rest: Tidy up, Examples (Learn / See a mistake caught), Undo change, Safe updates switch.
  - Labels shorten under 1120 px.
- **Completion:** Ctrl+Space, after a ".", or after two letters. It offers the columns of the tables in the command, then tables, databases and SQL words.
- **Index:** a database picker, tables with row counts, columns with friendly types, a "first 200 rows" button, and double-click to insert a name.
- **Results:**
  - Virtualized ruled grid with numbers sorted as numbers and a NULL chip.
  - Copy includes the headers.
  - Chips appear when there are several result sets.
  - Commands with no rows get a DONE line.
- **Error sheet** shows the explanation, the server's own text, Fix, Show me (jumps to the line), and Copy error.
- **Questions** use a ChoiceSlip. On a red slip Enter never confirms, because the focus is on Cancel.
- **Receipt tape** for every run, with an ERROR stamp on failures. Clicking a receipt jumps to that command.
- **Saving:**
  - The script is auto-saved to `config\notebook.sql` (atomic, 1 s after typing stops, and on close).
  - The Safe updates setting and the last database are kept in `config\notebook.json`.
- **Elsewhere:**
  - Today's database rows open in the Notebook.
  - Today refreshes after data changes.
  - Ctrl+R reloads the schema while in the Notebook.
  - The old Ctrl+S server toggle was removed, so it can't stop the server by accident.

**Checked:**
- **Tests:** 141, all passing (`dotnet test`). Of these:
  - 54 are SQL unit tests.
  - 4 are Notebook integration tests on real 11.8 and 10.4: the college script, explained errors, a restart, the row cap, Undo of each change kind, and pruning.
- **By hand in the real app** (UI Automation and PrintWindow captures, 1366×768 and 1000×640, MariaDB 11.8):
  - The college script ran with Run all.
  - Sinhala text round-trips.
  - The margin notes appear for the mistakes script.
  - `DELETE FROM marks`: the slip showed "6 rows", Run anyway deleted them, Undo put back all 6, and a count confirmed it.
  - Duplicate key gives the "That id is already used" sheet.
  - The text is unchanged across a close and relaunch.
- **Bugs found this way and fixed:**
  - NULL cells were blank (DBNull trigger → converter).
  - Toolbar buttons had no accessible names.
  - The ERROR stamp covered the receipt text.
  - Stop appeared while a question was open.
  - Run could start from the keyboard behind an open slip.
  - Notes piled up when the view first opened.
  - The switch was clipped at 1000 px.

## Step 4: Tables (DONE, 2026-11-24)
Browse and edit rows, design tables, run table operations, and edit views, procedures, functions, triggers and events (SPEC §5.4).

**Built:**
- **Core (`LuminaDB.Core/Tables`):**
  - `TableDesign` reads a table (columns, keys, indexes, foreign keys, checks, triggers) and `DdlBuilder` writes CREATE or ALTER for it.
  - MariaDB's way of reporting things is evened out so an unchanged table gives an empty ALTER: collations equal to the table's, `0` vs `0.00`, DEFAULT NULL, and CHECK text the server rewrites.
  - `DesignChecker` marks problems before anything runs (AUTO_INCREMENT without a key, a VARCHAR with no length, a foreign key to a missing column, …).
  - `RowQuery` (paging, search, filters, server-side sort), `RowSql` (INSERT and UPDATE by key, value checks for NOT NULL, length, numbers, dates, ENUM), `TableOps` (rename, copy, move, empty, drop, collation, CHECK, ANALYZE, OPTIMIZE, REPAIR), and `DbObjects` (list, read, templates and drop for the other objects).
- **Tables screen (F4):**
  - **Index:** the database picker, a filter, New table, and More (new view, procedure, function, trigger or event from a template), grouped by kind with counts.
  - **Rows:** 100 per page, search across every column, a filter builder, sorting on the server, NULL chips, and the exact SELECT shown underneath.
  - **Row form:** a field per column with the right editor (text, long text, ENUM list, yes/no, date picker, foreign-key lookup showing the row it points at). It checks every value before sending, shows the SQL it will run, and names the field to fix. A required field nobody filled in is flagged instead of silently sent as `''`.
  - **Structure:** the column grid (type, length, default, PK, NN, UQ, AI, UN, comment, move, remove), keys and indexes, foreign keys with ON DELETE and ON UPDATE, checks, and the table's triggers. "The SQL it will run" updates live; Apply asks before dropping columns.
  - **Operations:** rename, copy (with or without rows), move, convert collation, Check/Analyze/Optimize/Repair with plain-English results, and Empty or Drop. Each shows its SQL and the risky ones ask first; the tables that point here are listed.
  - **Definition:** the SQL editor with live underlines and margin corrections, Save, Put back (the version before the last save), Tidy (views), and Drop. A dropped object's text stays in the editor so Save makes it again.
  - Every change runs through the Notebook session: one Undo copy per change and the same explained errors.
  - Keys: Ctrl+S saves the open form, structure or definition; Esc closes the form; Delete and Enter work on the grid; Ctrl+F jumps to search; Ctrl+R reloads.
- **Shared with the Notebook:** `SqlEditorSetup` (editor look and checks) and `GridKit` (cell and header templates).

**Checked:**
- **Tests:** 213, all passing (`dotnet test`). 75 are for Tables: unit tests plus integration tests that design, create, alter, round-trip, edit rows and manage objects on real 11.8 and 10.4.
- **By hand in the real app** (UI Automation and PrintWindow captures, 1366×768, MariaDB 11.8): browsed `courses` and `students`; added a row; saw the required-field check; changed a column length and comment through Structure and applied it; read the Operations page; created a view from the template; dropped it; took all four changes back with Undo. Screenshot: `docs/step4-tables.png`.
- **Bugs found this way and fixed:**
  - An empty New row was sent as `name = ''`.
  - Rename showed `RENAME … TO` the same name.
  - The Undo question read "Puts back removes new …" (now "Back to how it was at 01:02: the new college.x is deleted").
  - After Undo removed the object being edited, the Definition tab disappeared.
  - Closing the app after the server had crashed threw an error and left the window stuck (Close ran inside Closing).
  - The emoji key marker in grid headers became "· key" in the type line.

**Known limit:** the integration tests, and the app's own leftover-server cleanup, stop any `mariadbd.exe` started from the same engine folder. Don't run `dotnet test` while the dev copy of the app is running; installed copies each have their own engines, so this doesn't affect users.

## Step 5: Projects and the web stack (DONE, 2026-11-24)
PHP projects served from LuminaDB, with their own database, logs, settings, snapshots, demo mode and submission pack, plus the mail catcher (SPEC §5.2, §5.11).

**Design:**
- **One web port** (prefers 80, bound to 127.0.0.1 only). Caddy is in front. A project with an `.htaccess` is passed by Caddy to an Apache that starts only while such a project exists, on an internal port (prefers 8800). Apache uses the same php-cgi pool through mod_proxy_fcgi. Sets without Caddy put Apache in front for everything.
- **php-cgi pool:** `php-cgi -b 127.0.0.1:<port>` with 4 children, recycled every 500 requests, restarted by itself up to 3 times a minute. PHP, Caddy and Apache run in a Job Object, so they die with the app; MariaDB does not.
- **Generated, never edited by hand:**
  - `config\php-<set>.ini`: absolute `extension_dir`, only extensions whose DLL exists, SMTP to the mail catcher, `display_errors` off, and our error page as `auto_prepend_file`.
  - `config\Caddyfile` and `config\httpd.conf`. Caddy reloads through its admin API (`/load`); dotfiles and `vendor/` return 403.
- **Addresses:** `http://<name>.localhost/` plus `http://localhost/<name>/` for older browsers, and an index page at `http://localhost/`.
- **Per-project PHP settings** go in the project's `.user.ini` (PHP rereads it within 5 seconds). Extensions are shared, because one PHP pool serves every project.
- **Project metadata** is kept in `config\projects\<name>.json`, so the project folder stays clean for submission.

**Built:**
- **Core:**
  - `Web/`:
    - `WebStack` finds the binaries (`engines\<set>\` or `LUMINA_WEB_DIR`). `WebStackService` handles start, stop, applying the project list without a restart, restarting PHP, and demo mode.
    - `PhpConfig` and `WebConfig` write the ini and config files. `LogTail` reads logs that Caddy and Apache keep open. `LanAddress` finds the PC's network addresses. `QrCode` is a QR encoder covering versions 1–40.
  - `Web/Resources/prepend.php` is the error page:
    - Fatal errors and uncaught exceptions get a full Ledger-style page: file, line, the code around it, a plain explanation (about 30 rules: undefined variable, the removed `mysql_*` functions, Access denied, unknown table or column, duplicate entry, headers already sent, …), and the stack.
    - Warnings get a strip at the end of the page.
    - JSON and XHR requests get JSON.
    - Visitors on another PC, or in demo mode, see a page with no code.
  - `Projects/`:
    - `ProjectService` creates projects from templates (Empty, CRUD, Login and registration, REST API; Laravel and WordPress when Composer or the WordPress zip is in the package). Creating also makes the database, imports, renames, sends deleted folders to the Recycle Bin, and opens projects in an editor.
    - `ProjectArchive` handles snapshots (code zip plus database dump; restore code, database or both, after saving the current state) and the submission pack (`StudentID_project_date.zip` with the code, `database/<db>.sql` and a README).
    - `DatabaseDumper` wraps mariadb-dump and the mariadb client. `Composer` runs the bundled phar.
  - `Mail/`: `MailCatcher` is an SMTP server on 127.0.0.1 (prefers 2525) that saves `.eml` files in `mail\`. `MimeMessage` handles multipart, quoted-printable, base64, RFC 2047/2231 and inline `cid:` images.
- **Projects screen (F2):**
  - **Index:** the web status sentence with Start/Stop, New project, Import folder, the project list, and a "rename" offer for folders whose names can't be web addresses.
  - **Header:** the address, Open in browser, open in VS Code, Notepad++ or Explorer (whichever exists), and Database in Notebook.
  - **Overview:** both addresses, which server serves it, connection details, the folder, Delete (folder to the Recycle Bin, database optional), and the submission pack.
  - **Logs:** PHP errors and requests, tailed live.
  - **PHP settings:** upload size, memory, time limit, time zone and deprecations for the project, plus the shared extensions with Save and restart.
  - **Composer:** a command line plus one-click install, update, dump-autoload, show and version.
  - **Snapshots:** take with a note; restore or delete.
  - **Demo:** LAN address, minutes, a firewall warning, then the address, a QR code and the auto-off time.
- **New project slip:** a name with live checks (it becomes the folder, the database and the address), the template list showing why Laravel or WordPress is unavailable, and progress while it is made.
- **Mail screen (F11):** the inbox with new-mail dots and a red tab dot, the message as sent (sanitised HTML in a WebBrowser, hidden while a slip is open), plain text, source, attachments (Save…), Delete and Delete all, and the SMTP settings to copy for PHPMailer or Laravel.
- **Today:** a web server card with the status, the mail catcher port, Start everything, Start/Stop web, and Open in browser. The web server starts with the database by default and restarts PHP if the database port changes.
- **Folder watcher:** a folder pasted into `projects\`, or an `.htaccess` added or removed, is served within about a second. Apache starts or stops by itself.
- **Shared styles** moved to `Theme/Pages.xaml`: tool buttons, page chips, index rows, tiny buttons and cards.

**Checked:**
- **Tests:** 232, all passing (`dotnet test`, 2 min 49 s).
  - 18 unit tests: ini, Caddyfile and httpd.conf generation, names, `.user.ini`, time zones, access-log parsing, MIME, SMTP and QR. The QR codes were decoded with OpenCV by hand: a URL, version 10+, and UTF-8 at High ECC.
  - 1 end-to-end test on real Caddy 2.11.9, PHP 8.5.13, Apache 2.4.71 and MariaDB 11.8. It covers:
    - every built-in template;
    - the error page: fatal, exception, warnings, JSON, syntax error and the demo header;
    - the PHP and access logs, `.user.ini`, and `mail()`;
    - a project through Apache and back;
    - snapshot and restore, and the submission pack (no `uca1400` collations, no vendor);
    - delete to the Recycle Bin, restarting PHP, and a clean stop.
- **By hand in the real app** (background UI Automation and PrintWindow, 1366×768):
  - The web server started with the database on `http://localhost/`.
  - Created `my-shop` from the CRUD template; both addresses returned 200.
  - PHP warnings and requests appeared in Logs.
  - A `mail()` with HTML and a `<script>` was caught and shown without the script.
  - The Delete-all slip hid the mail preview.
  - Adding an `.htaccess` started Apache, which served a RewriteRule; removing it stopped Apache.
  - Took a snapshot. Saved PHP settings, which wrote `.user.ini`.
  - Closing the app left no PHP, Caddy or Apache process.
  - Screenshot: `docs/step5-projects.png`.
- **Bugs found this way and fixed:** underscores in extension names and checkbox text were eaten as access keys (`pdo_mysql` showed as `pdomysql`); "Delete all 1 messages?"; template rows of different widths in the slip; the copy icon sat far from the address.

**Known limits:**
- Extensions are global: one PHP pool serves every project.
- A path-style address (`localhost/<name>/`) to an `.htaccess` project can break an absolute `RewriteBase`; the `<name>.localhost` address always works.
- Demo mode was tested through generated config and the demo header, not by binding a real LAN address, which would raise a Windows firewall prompt.
- phpMyAdmin is left for step 9.

**Composer and WordPress added (2026-11-24):**
- Composer 2.11.2 and WordPress (latest zip) were downloaded into the dev bundle (`ParityTests\web\bundle\composer\composer.phar` and `templates\wordpress.zip`), with checksums matching getcomposer.org and wordpress.org. They are not in git; packaging (step 10) must ship them in `engines\<set>\composer\` and `engines\<set>\templates\`.
- **WordPress, by hand:** created `wp-blog` in the app. The installer connected to `wp_blog`, `install.php?step=2` returned "Success!", the home page loaded, and WordPress's welcome email landed in Mail.
- **Laravel, by hand:** created `lara-app` in the app (about 2 minutes, needs the internet). The first try returned 500: Composer's post-install script migrates an SQLite file before LuminaDB points `.env` at MariaDB, so `sessions` didn't exist.
  - Fixed: after writing `.env`, LuminaDB runs `php artisan migrate --force` against the project's database, sets `APP_URL` to `http://<name>.localhost`, and removes the empty `database.sqlite`.
  - `Composer.RunScriptAsync` runs any PHP script with LuminaDB's php.ini, logged to `logs\composer.log`.
  - After the fix, both addresses return 200, through Apache because of `public/.htaccess`. The Composer tab ran `--version`.
- **Also fixed:** a timed-out port probe in `CanConnectAsync` left an unobserved exception in `app.log`.
- **Tests:** 232, all passing.

**Notebook snippets added (2026-11-24, SPEC §5.1 "saved snippets"):**
- **Use:** type a short word, then Tab, to write a whole statement. Snippets also show in the word list (picked when you type the whole word) and in a new **Snippets** menu next to Examples. A statement picked from the menu gets its own line.
- **Built-in (19):** `sel`, `selo`, `selj`, `selg`, `cnt`, `ins`, `insm`, `upd`, `del`, `cdb`, `ct`, `ctfk`, `addc`, `fk`, `idx`, `cview`, `tx`, `proc`, `cuser`. No word collides with an SQL word, and the SQL checker accepts every body (a test enforces both).
- **Blanks:** `${1:text}`. Tab and Shift+Tab move by number, not by position (so `sel` asks for the table first). The same number twice is typed once and copied. Tab after the last blank, or Enter, jumps to `${0}`; Esc leaves. Blanks are drawn as dotted fill-in lines in Ledger blue, not AvalonEdit's green.
- **Your own:** "New snippet…" (or "Save the selected SQL…") opens a slip with the word, a name and the SQL. Words are checked: 2 to 20 letters, not built-in, not an SQL word. Saving a word you already have replaces it. Stored in `config\snippets.json`. Only `${n}` is special, so `DELIMITER $$` saves as written.
- **Code:** `Core/Sql/SqlSnippets.cs` (parser, built-ins, store). In the app: `SnippetWriter`, `SnippetSession` and `SnippetBlanks` in `Editor/SqlEditorKit.cs`, and `SaveSnippetSlip`.
- **Checked by hand** (background, posted keys only): menu insert; `cnt` + Tab; blanks in number order; the copied blank; Enter and Esc; saving, replacing and typing your own snippet. The "Delete one of yours" submenu couldn't be opened from the background (WPF submenus need a focused window), so delete is covered by tests only.
- **Tests:** 253, all passing (`dotnet test`, 2 min 43 s).

**Polish and a fix (2026-11-24, from using it):**
- **Word list:** opens on the first letter. It lists only words that *start* with what you typed (AvalonEdit's own filter also listed `TO_BASE64` for "se" and preferred lowercase snippet words over keywords). An exact word is picked, otherwise the first in our order. Enter or Tab inserts the highlighted item (tried "one letter + Enter = new line" for aliases; it got in the way, so it was removed). Esc or Space keeps just what you typed.
- **Readability:** the chosen row in the word list is an amber highlighter mark with an ink bar, not Windows blue. Selected text in the editor is a stronger blue tint. Snippet blanks are drawn above the current-line tint, and the blank you're in has a solid blue rule.
- **IDE-style typing (Notebook and the Tables definition editor):** Enter after an open `(` indents the next line by 2; deeper lines keep the line above's indent; Enter between `(` and `)` puts `)` on its own line under an indented empty line; a `)` typed at the start of a line lines up with its `(`. The cursor next to a bracket boxes it and its partner in amber (a stray `)` in red). Brackets in strings, quoted names and comments are ignored, and `;` ends a statement (`Core/Sql/SqlBrackets.cs`, 12 tests). Checked in a test copy by typing a CREATE TABLE and an INSERT.
- **Side tabs:** labels read left to right (tabs are 84 px wide; the chosen one 94 px).
- **Bug fixed: a second LuminaDB stopped the first one's servers.** On start, leftover clean-up adopted or killed *any* `mariadbd`/`httpd`/`caddy`/`php-cgi` running the same exe, even one started by another LuminaDB folder that shares the engine. Now a process only counts as ours when its command line names our folder (`ManagedProcess.StartedFrom`, via WMI). The integration tests' own clean-up had the same flaw and now only touches their own folder, so `dotnet test` no longer kills a dev copy that is open. New tests: `A_process_belongs_to_the_folder_its_command_line_names`, `Another_LuminaDB_folder_sharing_the_engine_never_touches_our_server`.

## Step 6: Doctor, backups, snapshots (DONE, 2026-11-24)
- **Backups (`Core/BackupService.cs`):** one plain `.sql` dump per database in `backups\<db>\<stamp>.sql`, with a `.json` beside it (when, why, kind, size, engine version). "Changed" means the database's information_schema fingerprint differs from the one saved at its last backup (`backups\state.json`), so unchanged databases are never dumped twice.
  - **When:** 2 minutes after the server starts, then every 30 minutes (changed databases only); before DROP DATABASE/TABLE, TRUNCATE, DELETE/UPDATE without WHERE and ALTER … DROP COLUMN, whatever Safe updates is set to (`SafetyGate.DatabasesToBackUp`), in the Notebook and in Tables; before the Doctor's repair; when LuminaDB closes (at most a minute).
  - **Keeping:** the newest 20 of each database plus the newest of each day for 7 days. Ones you take yourself are never pruned.
  - **Restore:** backs up the current state first, empties the database (so tables made later go too), replays the dump, and puts the safety copy back if the replay fails. A deleted database can be brought back.
  - If a backup before a risky command fails, a slip asks "Run anyway" or Cancel.
- **Doctor (`Core/Doctor.cs`, F9):** server state, engine files (missing or emptied by antivirus), port, firewall, leftover servers from this folder, system tables (`mariadb-check`), clock and time zone, backups, server settings, folder path, disk space, writable folder. Fixes: start or restart the server, stop leftovers, repair system tables (backs up every database first), back up now, open the engine folder. A fix that worked gets a FIXED stamp and the checks run again. It runs after every start and when the tab opens; the tab's red dot covers both Today's quick checks and the Doctor.
- **Backups page (F10):** databases on the left (also deleted ones that still have backups), their backups on the right with kind stamps (AUTO, BY YOU, BEFORE CHANGE, ON CLOSE, BEFORE RESTORE), Restore, show file, delete; "Back up everything now"; the schedule and the next check time. Project snapshots (taken in Projects) are listed underneath with "open in Projects".
- **Checked in the test copy** (background UI Automation only): Doctor flagged the un-backed-up `test` database, "Back up now" fixed it and stamped it; a Notebook `DROP TABLE test.t1` made a "Before DROP TABLE test.t1" backup and then dropped the table; restoring it from the Backups page brought both rows back and saved a "before restore" copy; the timed backup caught a change made from outside.
- **Tests:** 278, all passing (`dotnet test`, 3 min 29 s). New: `BackupDoctorTests.cs` (retention, check parsing, engine-file problems, backup/restore on both engine sets, Doctor checks and fixes on a real server) and `Risky_commands_name_the_databases_to_back_up_first`.

**Window polish (2026-11-24, from using it):**
- **Tabs no longer jump.** Clicking a tab gave it keyboard focus, which added a 2 px border; the tabs shared one row height, so the whole stack moved. Now every tab has one fixed height (fitted to the window), and keyboard focus is a drawn ring that never changes the layout. Checked: tab positions are identical whichever tab is selected.
- **Tabs:** F-keys printed on the tab (no tooltip covering the tabs below), the alert dot sits beside the label instead of on the corner, hover brings a tab forward a little.
- **Title bar:** LuminaDB's own, on the dark desk, showing the open section (the white Windows bar clashed). Drag, double-click, snap, Alt+Space and edge resizing still work; maximised windows are padded back on screen.
- Today's CHANGED column no longer cuts off the time.

## Step 7: Import and export (DONE, 2026-11-24)
- **.sql import (`Core/Import/SqlImporter.cs`):** reads `.sql`, `.sql.gz` and `.zip` of any size as a stream (`SqlStreamSplitter`: the mysql client's rules, DELIMITER blocks, version comments), runs statement by statement on one connection, keeps going past errors (like `--force`), stops on a lost connection or after 100 errors, reports progress and can be stopped. A file that names no database goes into one you choose. Databases it touches are backed up first.
  - **MySQL 8 / MariaDB 11 fixes (`ImportRewriter.cs`):** RESULTS.md §2 rules R01–R24 applied per statement (Workbench VISIBLE/INVISIBLE indexes, caching_sha2 users, 0900 and uca1400 collations on the 10.4 set, `->>`, REGEXP_LIKE, CHECK … NOT ENFORCED, `AS new ON DUPLICATE KEY`, TABLE t, VALUES ROW(), CAST AS JSON, SET PERSIST, SRID, role hosts, dump-header lines). Text inside strings is masked first, so data is never changed. Features with no safe rewrite (LATERAL, JSON_TABLE, GROUPING, functional indexes, MEMBER OF…) fail with a plain explanation of what to write instead. The preview lists every change with its line before anything runs.
- **CSV and Excel into a table (`TableImporter.cs`, `Xlsx.cs`):** delimiter, header and encoding (UTF-8 or Windows-1252) detected; a type guessed per column (INT, BIGINT, DECIMAL, DOUBLE, DATE, DATETIME, TIME, VARCHAR, TEXT; numbers with a leading 0 such as phone numbers stay text; dates day-first unless the data proves month-first). New table (with an `id` key added, or the file's own unique id made the key) or add to an existing table by column name. 500 rows per INSERT; a failing batch is retried row by row so one bad row costs only itself. `.xlsx` is read and written without extra libraries (shared strings, date styles).
- **Export (`DataExporter.cs`):** `.sql` via mariadb-dump (one table, a database or all), optionally made XAMPP/MySQL 8/phpMyAdmin-friendly (sandbox line removed, uca1400 → utf8mb4_unicode_ci); CSV (UTF-8 with BOM so Excel shows Sinhala and Tamil), JSON, and Excel with one sheet per table. Files go to the `exports` folder.
- **Import page (F6):** three pages (.sql file, CSV or Excel → table, Export), drag-and-drop, progress with Stop, results with each failed command and why. "Open with LuminaDB" on a `.sql`/`.csv`/`.xlsx` opens it here.
- **Found by the tests and fixed:** the stream splitter misread scripts when the reader returned small pieces (a look-ahead index went stale when the buffer moved: every real file is read in pieces); R02 also rewrote invisible *columns* (MariaDB syntax) as if they were indexes; a new table's own `id` column didn't become its key.
- **Checked in the test copy** (background UI Automation only): opened `C10_mysql8_misc.sql`: twelve fixes listed, import ran 56 of 58 commands and explained the two that can't run (functional index, MEMBER OF); a CSV with day-first dates, phone numbers, a quoted comma and Sinhala made `c8.marks` correctly; exporting `test` as XAMPP-friendly `.sql` changed three MariaDB-only lines.
- **Tests:** 312, all passing (`dotnet test`, 5 min 23 s). New: `ImportExportTests.cs` (rewriter rules and the string-masking guarantee, the splitter at every chunk size, CSV/JSON/Excel round trips, type guessing, and on both engine sets: the Workbench/phpMyAdmin/MySQL 8 parity scripts, an 11.8 dump into 10.4, export → import round trips, a bad row in 1,201).

## Step 8: Diagram (DONE, 2026-11-25)
- **Reverse engineering (`Core/Diagram/`):** `DiagramReader` reads every base table of a database (columns, keys, foreign keys). `DiagramLayout.Arrange` places parents on the left and the tables that point at them to the right, rank by rank, ordered within each column to sit near their parents. Very tall columns wrap, and tables with no relations are packed at the end. Cycles and self-references are handled.
- **Relations (`DiagramGeometry`):** orthogonal lines run from the FK column's row to the key's row, with crow's-foot ends:
  - The many side is a crow's foot. A unique FK (1:1) gets a bar instead.
  - A nullable FK is drawn dashed, with a circle on the parent side.
  - Lines go round cards that sit between their two ends (5-segment detours when needed).
  - Card sizes are computed from the monospace text, so the WPF canvas, SVG and PDF all match.
- **Diagram file:** stored as `config\diagrams\<db>.json`. It holds:
  - positions (kept after dragging)
  - tables drawn or edited on the diagram but not applied yet
  - tables marked to drop
  - hidden tables

  A table renamed on the diagram keeps its relation lines. The database last shown is remembered.
- **Forward engineering and sync (`DiagramSync`):** the diagram is compared with the live database, and the script is built in this order:
  1. CREATE for new tables, parents first. FKs inside a cycle of new tables are added afterwards.
  2. RENAME TABLE before ALTERs. The server moves the FKs that point at a renamed table, so no-op FK changes are dropped from the script.
  3. ALTER for changed tables.
  4. DROP, children before parents.

  Blockers stop the script from running: name clashes, relations to tables that won't exist, keeping a child of a dropped table, and the table design checker's errors. "Add relation" creates `parent_id` with the key's type, an index and the FK.
- **Diagram page (F5):**
  - Database picker, Arrange, New table, zoom (Ctrl+wheel, Fit/100%).
  - Drag cards; the position is saved.
  - Side panel with table details: Edit, Open in Tables, Hide, Drop / Delete / Keep, Undo changes.
  - Table editor: columns with type, length, NN, PK and AI; relations with ON DELETE/UPDATE; add a relation to any table; design tips.
  - Amber "not in the database yet" bar.
  - Review script, then Run on <db>. A backup is taken first, and dropping asks first. Copy and Save .sql are also there.
  - Export PNG (2×), SVG, PDF (vector, built-in Courier: non-Latin names print as "?", and the page says so) and a CREATE script (`CREATE DATABASE` + every table in an order that runs), all to `exports`.
  - New-database box when there are none.
- **Found by testing and fixed:** a line could run behind a card between its ends (departments→courses under staff), so routing now avoids cards. `DdlBuilder.Alter` doesn't rename tables, so sync emits RENAME TABLE itself.
- **Checked in the test copy** (window kept at the bottom; never opened a dropdown):
  - A 7-table `college` schema (self-reference, composite PK, 1:1 card, optional FKs) showed 8 relations correctly.
  - PNG/SVG/PDF/.sql exports were written.
  - Drew `rooms` (+ column `code`): Review script showed one CREATE; Run made the table (checked with SHOW CREATE TABLE); drafts were cleared and the layout was kept.
- **Tests:** 337, all passing (`dotnet test`, 4 min 51 s). New: `DiagramTests.cs`. It covers:
  - layout: parents left, no overlaps, cycles, wrapping, new cards beside old
  - routing round cards, crow's-foot/1:1/optional marks
  - sync order, cycles, renames, blockers, drops, Add relation
  - SVG well-formed with Unicode; PDF xref offsets and inflated content; PDF string escaping
  - diagram file round trip and corrupt files
  - on both engine sets: forward-create → reverse-engineer → change with a cycle, a new FK and a rename → drop → CREATE script into a second database

## Notebook Focus mode (2026-11-25, between steps 8 and 9)
- **What it does:** the ⤢ button at the right end of the Notebook toolbar, or Ctrl+Shift+M, gives the editor the window. Esc or the button again leaves it.
  - The masthead and the activity line fold into one line in the title bar: status dot, state, port, queries/s and the Start/Stop button.
  - The binder tabs become a 34 px rail of coloured edges with F-keys. Hover shows the name; F1–F12 still work.
  - The table list and the receipt slide out. Two toolbar toggles pin either one back.
  - Toolbar in focus: Run, Run all, Tidy, **More** (Examples, Snippets, Undo, the panels, Leave focus) and the database name.
  - Three views: Editor | Split | Results. The divider is a grip: drag to share the height (dragging it nearly shut snaps to that view), double-click to cycle. Out of focus, double-click evens it out.
- **Motion:** `Controls/Motion.cs`, one ease-out tween (210 ms) used for every slide. It jumps straight to the end when Windows animations are off.
- **Remembered** in `config\ui.json` (`ViewModels/UiState.cs`): focus on or off, the view, the split, and the pinned panels.
- **Code:** `NotebookViewModel.Focus.cs`; `MainViewModel.IsCompactChrome`; `MainWindow.ApplyChrome`; `NotebookView.ApplyPanels/ApplyLayout`. The old GridSplitter resized the Auto verdict row, so it is now a Thumb that sets both rows itself.
- **Dev/test:** `LUMINA_OFFSCREEN=1` opens the window off every screen, not activated and not in the taskbar.

## Step 9: Users, Server, Settings, phpMyAdmin switch (DONE, 2026-11-25)
- **Users (F7, `Core/Admin/Accounts.cs`, `UsersViewModel`):**
  - Accounts and roles are read from `mysql.global_priv` (JSON: plugin, password, role, locked), because `mysql.user` has no `account_locked` column on 10.4/11.8. Role grants come from `roles_mapping`.
  - Privileges per database, table or column, as checkboxes with plain words, plus presets (read only, read and write, everything, nothing).
  - The changes are turned into REVOKE then GRANT statements (`Accounts.Diff`), shown before Apply, with Undo.
  - New user (or role): name, host, password shown in plain text, starting preset on a database. The page shows the SQL that will run and the PHP connection line. Passwords are sent as `@pwd` and masked in the shown script.
  - Change password, Lock/Unlock, Delete (asks first), give/take a role (and set it as the default role).
  - The root password is set or removed on every root account at once (Settings and Users).
  - caching_sha2_password is explained on the page and in the error explainer (1524). 1396 and 1227 are explained too.
- **Server (F8, `ServerMonitor`, `ServerViewModel`):**
  - Status tiles, refreshed every 2 s while the page is shown: uptime, connections, queries/s, traffic, buffer pool use and hit ratio, slow queries.
  - Connections list with Stop query (KILL QUERY) and Disconnect (KILL), each asking first. LuminaDB's own connection and MariaDB's threads can't be killed.
  - Variables: 658 of them, searchable, the explained ones first. Set now (SET GLOBAL) or Keep (saved to `config\my-<set>-overrides.json` and written under [mysqld]).
    - Unsafe ones can't be kept (port, datadir, bind_address, skip_grant_tables, log paths, …).
    - If the server then fails to start, the overrides file is set aside (`.failed-<stamp>.json`) and the server is started once more without it.
  - Logs: MariaDB error, slow, general, PHP errors, php-cgi, Caddy, Apache, app.log. The slow and general logs have on/off switches that take effect now and are kept.
  - Console tab (`Core/Admin/MariaDbConsole.cs`): the real `mariadb` client runs inside the page with no window (pipes, batch mode, `--table --force`). Text is UTF-8 both ways, so Sinhala and Tamil work. ↑/↓ recall earlier lines, `clear` empties it and `exit` closes it.
    - It closes itself when the server stops or changes port, and when LuminaDB closes. If LuminaDB dies, the client sees its input close and exits too.
    - "own window" still opens it in a separate console. The password is passed through MYSQL_PWD only.
- **Settings (F12, `AppSettings`, `SettingsViewModel`):**
  - Ports: database, web, PHP, mail. They are checked, saved to `config\settings.json`, and the remembered ports are cleared. The page then offers to restart now.
  - Start with Windows: a per-user `HKCU\...\Run` entry, `"LuminaDB.exe" --minimized`. It starts minimised.
  - Root password.
  - Automatic backups: off, 15, 30 or 60 minutes (the Care timer follows it).
  - Text size 100/110/125%: a LayoutTransform on the window, so the layout scales and nothing is clipped.
  - phpMyAdmin switch.
  - Data folder move: stop, copy, compare file count and bytes, switch (`config\paths.json`), start, check the databases are all there. On any failure it rolls back and deletes only what it copied. The old folder is kept as `.old-<stamp>`.
  - The engine set facts, with a note on the legacy set.
- **phpMyAdmin (`Web/PhpMyAdminConfig.cs`):**
  - Off by default. When on, it is served at `http://phpmyadmin.localhost:<port>` by Caddy, or by Apache in front. `/phpmyadmin` on localhost redirects there.
  - Private folders (libraries/, vendor/, setup/, tmp/, config files, dotfiles) return 403.
  - `config.inc.php` becomes a stub that loads LuminaDB's file through `LUMINA_PMA_CONFIG`, set in php-cgi's environment. The original is kept as `config.lumina-original.php`.
  - Login: straight in while root has no password. Once root has a password, phpMyAdmin uses cookie login and asks for it; the password is never written to disk.
  - `prepend.php` now leaves phpMyAdmin alone (it only acts on project files).
- **Found while checking and fixed:**
  - Cancelling the password slip didn't answer, so the page waited forever.
  - The table and column pickers went blank after the account list reloaded: WPF pushes null when a combo's list is cleared, so the "(every …)" entry is now never removed.
- **Checked in the test copy** (off-screen, self-snapshots only; no dropdown opened, the console never launched):
  - All three pages render.
  - Created `shop_app@localhost` with the read-and-write preset on `c8`, ticked CREATE and applied it. SHOW GRANTS matched.
  - The root password slip and the user password slip open and cancel.
  - Text size 125% scales the whole window.
  - The Connections, Variables and Logs tabs work.
- **Tests:** 357, all passing. New: `AdminTests.cs` (22). It covers:
  - account SQL, names, Diff, password masking
  - variable normalising, status maths, overrides
  - settings, the data-folder check, the Windows start-up entry (in a test key)
  - phpMyAdmin config
  - on both engine sets: users and roles end to end, KILL, SET GLOBAL
  - saved ports, a real data-folder move
  - phpMyAdmin through Caddy: straight-in login, 403 on private paths, the redirect, and cookie login once root has a password
- **Recheck before step 10 (2026-11-25):**
  - Clean Release build: 0 warnings.
  - All 12 pages snapshotted off-screen.
  - The Step 9 code was reviewed against SPEC §5.7, §5.8, §5.9, §5.12 and §5.13. Fixed:
    - The console is now in a tab (SPEC §5.8). Before, it opened a separate window.
    - The Doctor now reports kept server settings, and says so when they were put aside (SPEC §5.9).
    - Saving a new database port with "Restart now" no longer starts a web server you had stopped.
  - 2 new tests: the console on both engine sets (SQL, an error, Sinhala/Tamil over two lines, exit), and the Doctor's report on kept settings.
- **Decided not to build:** a dark theme and a Sinhala/Tamil interface (SPEC §5.13 updated). Sinhala/Tamil *data* (utf8mb4) is unchanged.

## Visibility and accessibility pass (2026-11-25, before step 10)
Decided: no dark theme and no translated interface. The work goes into making what exists easy to see and use (SPEC §5.13, §7).
- **Contrast (WCAG AA, 4.5:1 for text), measured for every text colour on every background it sits on.** Four colours failed and were darkened, keeping their hues:
  - Ink3 grey #857D6C (3.5) → #625B4D (5.8)
  - Red #D83A1E (4.0) → #AE2F18 (5.6; white on red 6.6)
  - Green #2F7A45 (4.1 on darker paper) → #276638
  - Amber #B8730A (3.3) → #835104
  - The same fixes went into the SQL editor colours, the grid headers, the diagram export, the dark title bar (#A39B89, 6.3 on ink) and the PHP error/info pages.
- **Text size:** nothing is smaller than 11 px now; 104 labels were 9–10.5 px. The binder tabs are 10 px wider so names aren't cut ("Notebook", "Backups"), and the F-keys on them are drawn at full strength.
- **Keyboard:** tiny buttons, segmented choices (page tabs, backup/text-size choices) and page chips now show a blue ring or underline when they have keyboard focus; before, they showed nothing. Disabled buttons are drawn at 55% instead of 35–45%, so they can still be read.
- **Screen readers:** an audit of every interactive XAML element found 5 without a usable name (Notebook table rows, result chips, "Show me", the Projects refresh icon, the Tables fix link). All are named now.
- **Not checked:** the focus rings were not seen on screen (the off-screen test copy never gets keyboard focus), and no real screen reader was used.
- **Nothing cut off (2026-11-25):** the Backups Restore button lost its left edge (the actions column was a fixed 170 px; the buttons need about 181). A new dev check, `Controls/LayoutAudit.cs`, now runs with every LUMINA_SNAP snapshot and lists each element WPF had to clip. It was run over 22 page states and found three more problems, all fixed:
  - Backups: the actions column sizes to its buttons and is shared between the header and every row.
  - Checkbox tick: 2 px wider than its box. This showed on Tables, Import and Users.
  - Diagram card rows: 2 px cut at the bottom. The card height is now a minimum, so the rows always fit.
  - After the fixes the audit reports nothing on any page.

## Beginner traps fixed (2026-11-25, before step 10)
Three gaps found by trying the app the way a student would.
- **Run vs Run all.** A command that uses a table created higher up the same script, before that line has run, used to show OK. Run then failed with "no table".
  - The checker now records, for each command, what it needs from earlier lines: tables or a database created above (and not on the server), a REFERENCES parent, or a USE above when the Notebook is in another database (`ScriptNeed` in SqlGuard).
  - The verdict bar shows **NOT YET** ("tbl_student is created above but not run yet. Press Run all, or:") with a **Run from line N to here** button.
  - When the command fails anyway, the error sheet starts with the reason and offers the same button.
- **Notebook sheets.** Tabs above the editor: **New** (Ctrl+T), close (Ctrl+W), **Open…** a .sql file (Ctrl+O), **Save** (Ctrl+S) and **Save as…** (Ctrl+Shift+S).
  - Every sheet keeps its text in config between runs (`notebook-sheets.json` and `notebook-sheets\`). The old `notebook.sql` becomes Sheet 1.
  - A file sheet shows a dot when it has unsaved changes. Closing a sheet with unsaved SQL asks first.
  - Files over 4 MB are sent to Import, which streams them.
- **Save results.** The result header has "save as CSV / Excel / JSON" (`ResultExport`), saved in exports, plus "show file".
  - CSV has a BOM so Excel reads Sinhala and Tamil correctly. Number columns become numbers in Excel and JSON, except values with more than 15 digits, which stay text.
  - A repeated JOIN column name gets a suffix in JSON ("id_2").
- **Imported XAMPP folders find their database.** `ProjectDatabaseScan` reads the PHP: new mysqli / mysqli_connect (with $variables), mysqli_select_db, PDO dbname=, define('DB_NAME'), and Laravel's .env.
  - The project's database becomes that name (feedback_db), not one guessed from the folder name.
  - When the folder has a .sql dump, a question offers to open it in Import, with the database filled in. Import shows the file and backs up before loading.
- **Checked:**
  - 14 new tests (371 in all).
  - Off-screen run: NOT YET → Run alone fails with the explained error → "Run from line 1 to here" runs the 3 commands → a sheet switch → CSV, Excel and JSON saved and read back → new and close tab → the close question → restart restores the tabs and cursor.
  - The clipping check is clean on all pages.
- **Not checked on screen:** the Open, Save and folder-picker dialogs (they would open on top of the user's work), and the "Open in Import" question after a folder import. The scan behind it is covered by tests.

## The window (2026-11-25)
- It opens the way it was left: size, place and maximised are kept in `config\window.json` (`WindowPlace`), and the window is pulled fully back onto a screen if the title bar would be off it, or if that screen is gone.
- The first time: a laptop-sized screen (1366×768, or any screen at 125-150%) opens maximised; a bigger one gets 1280×800, centred. The minimum size drops to fit a smaller screen.
- A short window (under 660 px) folds the masthead into the title bar on every page, as Notebook focus does. Tables shows "New" beside "More" when narrow.
- Checked: 8 tests; every page at 980×600 and 1366×768 off-screen, with nothing clipped.

## Lighter on memory and idle (2026-11-25)
Measured off-screen, the same test copy before and after (working set / private bytes):
- Before: 504 MB / 934 MB (MariaDB 236 / 721, 5 php-cgi 120, Caddy 31, app 117).
- After, no projects: 244 MB / 575 MB. The web server isn't started until there's a project (or phpMyAdmin is on); the first project starts it.
- After, web running: 355 MB / 674 MB (MariaDB 128 / 485, 3 php-cgi 69).

What changed:
- MariaDB: `performance_schema=OFF` (nothing reads it); buffer pool capped at 128 MB (was 256).
- PHP: OPcache 64 MB; 2 workers on PCs with 8 GB or less (4 above).
- The root password is decrypted once and again only when its file changes (the activity line connects every second).
- The activity line stops while the window is minimised and slows to every 3 s behind other windows.
- Server start reads every database's tables once for both Notebook and Tables.
- Notebook grids keep 10,000 rows (was 100,000), with "show up to 100,000" for plain reads; a big old grid is let go before the next run.
- The unused Instrument Serif font is no longer embedded.
- Not changed on purpose: the Doctor still runs at start (Today shows it first); pages are still created at start; the projects watcher already ignores everything below a project's top folder.

## Faster page switches (2026-11-25)
- Measured with `LUMINA_TIMING=1` (logs each switch until the page is drawn and idle): every switch took 100-360 ms, because each visit rebuilt the page (Tables ~240, Diagram ~200, Users ~360).
- `PageHost` builds a page on its first visit and then keeps it hidden. Coming back now takes 8-25 ms; Users (~140), Projects (~70) and Backups (~57) still re-read their lists on purpose.
- Notebook (editor focus) and Mail (message view) do on return what they did on load. Database pickers on Diagram, Import and Users are updated in place (`ListSync`), so a kept page keeps its choice.
- Checked: all 22 page states off-screen, nothing clipped, pickers keep their values on return.

## Step 10: Packaging (2026-11-25)
- Two installers from one build (`packaging\build.ps1`, details in `packaging\README.md`): `LuminaDB-Setup.exe` (94 MB, Windows 10/11 64-bit, modern set) and `LuminaDB-Setup-Win7-32bit.exe` (108 MB, Windows 7/8/8.1 64-bit gets the legacy set, any 32-bit Windows the x86 set). The portable zip was dropped: the installers are the product.
- Sets trimmed to what LuminaDB runs (no debug symbols, headers, test tools, Apache manual): modern 230 MB, legacy 164 MB, x86 145 MB on disk. Legacy/x86 use PHP 8.2.36 and Apache 2.4.66 VS17, the newest that run on Windows 7; x86 uses MariaDB 10.4.22, the last 32-bit build.
- `check-imports.ps1` reads every program's import table: all DLLs are in the set or part of Windows, and the Windows 7 sets use nothing newer than Windows 7. MariaDB's app-local Universal CRT is copied beside PHP and Apache in those sets, so Windows 7 without the UCRT update needs nothing installed.
- Per-user install, no admin. Wrong PC: a clear message naming the right file. .NET 4.8 missing: downloaded from Microsoft during setup. Upgrade and uninstall close the running copy with the new `LuminaDB.exe --quit` (clean server stop). Uninstall keeps data unless the person says otherwise.
- The app picks the best installed set that runs here and prefers the one holding the databases (`EngineSetSelector.ChooseInstalled`), so a Windows 7 PC upgraded to 10 keeps its data. `LUMINA_ENGINE_SET` forces a set for testing.
- MIT licence (repository `LICENSE`), `THIRD-PARTY-NOTICES.txt` and each program's licence in `{app}\licenses`. Settings › About: version, Licences, and the one "Support LuminaDB ☕" link (also on the installer's last page). Version 1.0.0.
- "Re-extract LuminaDB" messages now say to run the installer again.
- Tested here: install, upgrade over a running copy (server and web up), uninstall, reinstall finding the old data, all three sets serving a PHP project that reads the database, wrong-PC refusal.

## Step 11: Full test pass (2026-11-25)
- Unit and integration tests: 393 pass (390 + `ReleaseSetsTests`).
- `ReleaseSetsTests` runs the three sets exactly as shipped (`packaging\out\stage`), laid out like an installed copy: first start, the MariaDB 11.8 dump imported with 0 problems into 11.8, 10.4.34 and 10.4.22, PHP projects served by Caddy (modern) or Apache (legacy, x86) and reading the database, all key PHP extensions loaded, phpMyAdmin and Composer working. The x86 set runs as 32-bit programs.
- Installers: install, upgrade over a running copy, uninstall, reinstall, wrong-PC refusal (see Step 10).
- Windows 7 and 32-bit: verified by binary scan (`check-imports.ps1`) and by running the sets on 64-bit Windows 10, not on a real Windows 7 or 32-bit PC (no legal Windows 7 image available).
- VirusTotal (2026-11-25): `LuminaDB-Setup.exe` (sha256 acfef762…59fc) 0 of 64 engines flagged it; `LuminaDB-Setup-Win7-32bit.exe` (sha256 d56aacfd…706d) 0 of 63.
- Clean PC (Windows Sandbox): not run; Sandbox is switched off on this PC and turning it on needs admin and a restart.

## Icon, publisher, tray, quick tips (2026-11-25, after step 11)
- New icon (the paper "L" with three slabs, `icons\svg\icon_2c_paper_tri.svg`). `packaging\make-icon.ps1` renders it with Edge at 16–256 px into `Assets\LuminaDB.ico`; 16, 24 and 32 px are separate drawings on the pixel grid (no ruled lines, dots or shadow), 20 px uses the 24 drawing. The script checks WPF and System.Drawing can read the .ico. Used for the exe, window, taskbar, installer, uninstaller and Apps & features.
- Publisher IzyaanTech: installer (`AppPublisher`, `VersionInfoCompany`, copyright), exe file properties (Company, Copyright), Settings › About ("LuminaDB 1.0.0 by IzyaanTech"), LICENSE "Copyright (c) 2026 IzyaanTech (awmhathif)".
- Tray, like XAMPP (`TrayIcon.cs`): closing the window keeps LuminaDB running by the clock (Settings › When I close the window: Keep running in the tray / Quit LuminaDB). The icon's dot is green (running), amber (starting/stopping), red (stopped by itself), grey (stopped); its tooltip gives the ports. Double-click opens the window; right-click: Open LuminaDB, Start/Stop the database, Start/Stop the web server, Open phpMyAdmin, Open the projects folder, Quit LuminaDB. One balloon the first time the window is closed into the tray. Start with Windows starts straight into the tray. Starting LuminaDB again shows the hidden window (named event `Local\LuminaDB-show-<id>`). `--quit`, Windows shutting down and the tray's Quit still close it for real, server stopped cleanly. Old settings files read as the defaults (the new bools are named so false is the default).
- Quick tips (`Tips.cs`, 45 tips, every page): one line under the page, the next tip on each visit and with "Next tip", × turns them off, Settings › Quick tips turns them back on. Folds away with the masthead in focus mode and short windows. Each tip names a real button or key on that page.
- Tested off-screen: close into the tray (process and server stay, window hidden), start again (window back), `--quit` while shown and while hidden (exit 0, server stopped), start in the tray with `--minimized` (server up, no window), Quit on close (closes, server stopped); the real tray icon created and updated with no errors (`LUMINA_TRAY=1`). Not tried by hand: the right-click menu and the balloon (they appear on the real screen).

## Error reports and command-line tools (2026-11-26)
- Unexpected errors now open a report box instead of a plain message: what happened, the report to read, Copy report, Save report (logs\reports) and Where to send it. LuminaDB keeps running; nothing is sent by itself. Settings › About › Report a problem makes the same report on request. An error that forces LuminaDB to close saves logs\reports\crash-*.txt, and the next start says so on the receipt tape.
- `ErrorReport` (Core): versions, Windows, engine set, the error with its causes and stack, the last 40 log lines. The Windows profile path, user name and PC name are replaced, and SQL MariaDB quotes (near '…'), values (Duplicate entry '…'), passwords and e-mail addresses are left out.
- `<LuminaDB>\bin` (`CommandLineTools`): php.cmd, composer.cmd, mariadb.cmd / mysql.cmd and mariadb-dump.cmd / mysqldump.cmd, rewritten at every start and server start so they point at the engine set and port in use (php with the web server's php.ini, or the usual extensions before the web server has run). Settings › Command line can put the folder first on the account's PATH (`UserPath`, HKCU\Environment); the uninstaller takes it out again (`ChangesEnvironment=yes`).
- New projects' config.php says, in a comment, that the project runs on LuminaDB and where those tools are, so an AI coding assistant reading the project uses them.
- Tests: `ReportAndToolsTests` (scrubbing, report contents, crash reports, tool files, php.cmd running with mysqli, PATH add/remove on a test registry key). Off-screen: the report box from a test error (password and SQL left out, saved, closed, LuminaDB still running), mariadb.cmd and php.cmd against the running copy. The PATH switch wasn't flipped on the real account.

## Fixes found while filming the walkthrough (2026-11-26)
- Tables › New row: picking a row from a linked column's list (product_id → "2 · Bread") left the value empty, because refreshing the list made the combo box push back an empty text. `FieldEdit` ignores text changes while it refills the list.
- PHP stopped at start with "Opcode handlers are unusable due to ASLR" on a PC where another PHP of the same build was running. php.ini now gives OPcache a name of its own (`opcache.cache_id`, from the data folder) and a file cache with fallback. Before: 3 starts out of 3 failed with 4 workers; after: 3 out of 3 ran.
- The PHP error page showed a stray warning from prepend.php (PHP 8.5 warns when `http_response_code()` follows PHP's own 500 header). It now sets the code only when it isn't 500 already.
- The browser's own /favicon.ico request went to index.php when the project has no favicon, so a page's code ran twice per visit (two rows, two emails). A missing favicon now gets 404, like Apache in XAMPP; files and pretty URLs are unchanged.
- Checked: Core tests 400/400; on the installed copy, the CRUD site, the error page and one caught email per visit.

## Not verified yet
- The tray's right-click menu and balloon, by hand on the real screen.
- Settings › Command line on the real PATH, and the uninstaller removing it.
- A real Windows 7 PC and a real 32-bit PC.
- A PC without .NET 4.8 (the download-and-install step in the installer).
- The "delete my data too" answer at uninstall (needs the dialog).
- Code signing (SignPath) and the GitHub release: not done; publishing waits for the go-ahead.
