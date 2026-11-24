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
- **Fonts** are embedded in the exe, so nothing is needed from the internet: Bricolage Grotesque, IBM Plex Sans/Mono and Instrument Serif. They are static TTFs, and the OFL licences are copied to `licenses\`.
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

## Next: Step 6: Doctor, backups, snapshots
The Doctor screen (checks with one-click fixes), automatic and manual backups, and the snapshot list across projects (SPEC §11).

## Not verified yet
- A real Windows 7 PC and a 32-bit PC (the X86 set uses MariaDB 10.4.22).
- A clean PC with no VC++ runtime installed (plan: Windows Sandbox).
