; LuminaDB installer (Inno Setup 6.7). One script, two installers:
;   ISCC /DEdition=modern LuminaDB.iss  ->  LuminaDB-Setup.exe              Windows 10/11, 64-bit (modern set)
;   ISCC /DEdition=win7   LuminaDB.iss  ->  LuminaDB-Setup-Win7-32bit.exe   Windows 7/8/8.1 64-bit (legacy set) and
;                                                                           any 32-bit Windows (x86 set)
; Run build.ps1 rather than ISCC directly: it builds the staged files first (build-sets.ps1).
;
; Per-user install into %LOCALAPPDATA%\LuminaDB: no administrator rights, no services. Upgrades keep the data,
; projects, backups and settings. Uninstalling keeps them too unless the person ticks "also delete".

#ifndef Edition
  #define Edition "modern"
#endif
#ifndef Stage
  #define Stage AddBackslash(SourcePath) + "out\stage"
#endif
#ifndef OutDir
  #define OutDir AddBackslash(SourcePath) + "out"
#endif

#if Edition == "modern"
  #define SetupName "LuminaDB-Setup"
  #define OtherSetup "LuminaDB-Setup-Win7-32bit.exe"
  #define EditionText "Windows 10 and 11 (64-bit)"
#elif Edition == "win7"
  #define SetupName "LuminaDB-Setup-Win7-32bit"
  #define OtherSetup "LuminaDB-Setup.exe"
  #define EditionText "Windows 7, 8 and 8.1, and 32-bit Windows"
#else
  #error Edition must be modern or win7
#endif

#define AppExe Stage + "\app\LuminaDB.exe"
#if !FileExists(AppExe)
  #error The staged app is missing. Run build.ps1 (or build-sets.ps1) first.
#endif
#define VerMajor
#define VerMinor
#define VerRev
#define VerBuild
#expr GetVersionComponents(AppExe, VerMajor, VerMinor, VerRev, VerBuild)
#define AppVersion Str(VerMajor) + "." + Str(VerMinor) + "." + Str(VerRev)

#define ReleasesUrl "https://github.com/awmhathif/LuminaDB/releases/latest"
#define SupportUrl "https://buymeacoffee.com/awmhathif"

[Setup]
; The same AppId for both installers: one "LuminaDB" entry in Apps & features, and either one upgrades the other.
AppId={{6A1F3C52-9D4E-4B7A-8E21-5C0D7B9A4F13}
AppName=LuminaDB
AppVersion={#AppVersion}
AppVerName=LuminaDB {#AppVersion}
AppPublisher=awmhathif
AppPublisherURL=https://github.com/awmhathif/LuminaDB
AppSupportURL=https://github.com/awmhathif/LuminaDB/issues
AppUpdatesURL={#ReleasesUrl}
AppCopyright=Copyright (c) 2026 awmhathif. MIT licence.
VersionInfoVersion={#AppVersion}
VersionInfoProductName=LuminaDB
VersionInfoDescription=LuminaDB installer for {#EditionText}
PrivilegesRequired=lowest
DefaultDirName={localappdata}\LuminaDB
DisableDirPage=yes
DisableProgramGroupPage=yes
DefaultGroupName=LuminaDB
UsePreviousAppDir=yes
; Windows 7 SP1 is the oldest Windows Inno Setup (and LuminaDB) runs on. The PC check below gives the clear message.
MinVersion=6.1sp1
OutputDir={#OutDir}
OutputBaseFilename={#SetupName}
SetupIconFile=..\src\LuminaDB.App\Assets\LuminaDB.ico
UninstallDisplayIcon={app}\LuminaDB.exe
UninstallDisplayName=LuminaDB
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
ChangesAssociations=yes
CloseApplications=yes
CloseApplicationsFilter=*.exe,*.dll
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[Messages]
FinishedLabel=LuminaDB is installed. Open it from the Start menu, or tick the box below to open it now.

[Tasks]
Name: "desktopicon"; Description: "Put a LuminaDB shortcut on the desktop"; GroupDescription: "Shortcuts:"

[InstallDelete]
; Engine programs are replaced as a whole on upgrade, so no file from an older version is left behind. Only the sets
; this installer brings; data, projects, backups and settings live elsewhere and are never touched.
#if Edition == "modern"
Type: filesandordirs; Name: "{app}\engines\modern"
#else
Type: filesandordirs; Name: "{app}\engines\legacy"; Check: UseLegacySet
Type: filesandordirs; Name: "{app}\engines\x86"; Check: not UseLegacySet
#endif

[Files]
Source: "{#Stage}\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#Stage}\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion
#if Edition == "modern"
Source: "{#Stage}\engines\modern\*"; DestDir: "{app}\engines\modern"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#Stage}\shared\*"; DestDir: "{app}\engines\modern"; Flags: ignoreversion recursesubdirs createallsubdirs
#else
Source: "{#Stage}\engines\legacy\*"; DestDir: "{app}\engines\legacy"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: UseLegacySet
Source: "{#Stage}\shared\*"; DestDir: "{app}\engines\legacy"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: UseLegacySet
Source: "{#Stage}\engines\x86\*"; DestDir: "{app}\engines\x86"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: not UseLegacySet
Source: "{#Stage}\shared\*"; DestDir: "{app}\engines\x86"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: not UseLegacySet
#endif

[Icons]
Name: "{userprograms}\LuminaDB"; Filename: "{app}\LuminaDB.exe"; Comment: "Databases, PHP projects and a local web server"
Name: "{userdesktop}\LuminaDB"; Filename: "{app}\LuminaDB.exe"; Tasks: desktopicon

[Registry]
; "Open with LuminaDB" for .sql, .csv and .xlsx (it opens on the Import page). Never takes over the default program.
Root: HKCU; Subkey: "Software\Classes\Applications\LuminaDB.exe"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "LuminaDB"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Applications\LuminaDB.exe\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\LuminaDB.exe"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\Applications\LuminaDB.exe\SupportedTypes"; ValueType: string; ValueName: ".sql"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\LuminaDB.exe\SupportedTypes"; ValueType: string; ValueName: ".csv"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\LuminaDB.exe\SupportedTypes"; ValueType: string; ValueName: ".xlsx"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\LuminaDB.sql"; ValueType: string; ValueName: ""; ValueData: "SQL script"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\LuminaDB.sql\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\LuminaDB.exe"",0"
Root: HKCU; Subkey: "Software\Classes\LuminaDB.sql\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\LuminaDB.exe"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\LuminaDB.csv"; ValueType: string; ValueName: ""; ValueData: "CSV file"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\LuminaDB.csv\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\LuminaDB.exe"",0"
Root: HKCU; Subkey: "Software\Classes\LuminaDB.csv\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\LuminaDB.exe"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\LuminaDB.xlsx"; ValueType: string; ValueName: ""; ValueData: "Excel workbook"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\LuminaDB.xlsx\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\LuminaDB.exe"",0"
Root: HKCU; Subkey: "Software\Classes\LuminaDB.xlsx\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\LuminaDB.exe"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\.sql\OpenWithProgids"; ValueType: string; ValueName: "LuminaDB.sql"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.csv\OpenWithProgids"; ValueType: string; ValueName: "LuminaDB.csv"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.xlsx\OpenWithProgids"; ValueType: string; ValueName: "LuminaDB.xlsx"; ValueData: ""; Flags: uninsdeletevalue

[Run]
Filename: "{app}\LuminaDB.exe"; Description: "Open LuminaDB now"; Flags: postinstall nowait skipifsilent

[UninstallDelete]
; Files the app makes while it runs. data, projects, backups, snapshots, exports, mail and config are kept unless the
; person asks for them to go (see CurUninstallStepChanged).
Type: filesandordirs; Name: "{app}\engines"
Type: filesandordirs; Name: "{app}\run"
Type: filesandordirs; Name: "{app}\logs"
Type: filesandordirs; Name: "{app}\tmp"
Type: filesandordirs; Name: "{app}\licenses"

[Code]
const
  NetReleaseKey = 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full';
  Net48Release = 528040;
  NetUrl = 'https://go.microsoft.com/fwlink/?linkid=2088631';
  NetFile = 'NDP48-x86-x64-AllOS-ENU.exe';
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

var
  DownloadPage: TDownloadWizardPage;
  NetDownloaded: Boolean;
  RestartAfter: Boolean;
  DeleteDataAsked: Boolean;

function WindowsMajor: Cardinal;
var
  V: TWindowsVersion;
begin
  GetWindowsVersionEx(V);
  Result := V.Major;
end;

{ Windows 7/8/8.1 with a 64-bit x64 Windows get the legacy set; every other PC this installer accepts gets x86. }
function UseLegacySet: Boolean;
begin
#ifdef TestSet
  { Test builds only (build.ps1 never sets it): force a set to try it on the build PC. }
  Result := '{#TestSet}' = 'legacy';
  Exit;
#endif
  Result := IsX64Compatible and (WindowsMajor < 10);
end;

function HasNet48: Boolean;
var
  Release: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM, NetReleaseKey, 'Release', Release) and (Release >= Net48Release);
end;

function RightPc(var Why: String): Boolean;
begin
#ifdef TestSet
  Result := True;
  Exit;
#endif
#if Edition == "modern"
  Result := IsX64Compatible and (WindowsMajor >= 10);
  if not Result then
  begin
    if WindowsMajor < 10 then
      Why := 'This installer is for Windows 10 and 11 (64-bit), and this PC runs an older Windows.'
    else
      Why := 'This installer is for 64-bit Windows 10 and 11, and this PC runs 32-bit Windows.';
  end;
#else
  Result := (WindowsMajor < 10) or (not IsX64Compatible);
  if not Result then
    Why := 'This PC runs 64-bit Windows 10 or 11, so it should use the main installer, ' + '{#OtherSetup}' +
           ', which has the newer database and PHP versions.';
#endif
end;

function InitializeSetup: Boolean;
var
  Why: String;
  Code: Integer;
begin
  Result := RightPc(Why);
  if not Result then
  begin
    if SuppressibleMsgBox(Why + #13#10#13#10 + 'Download ' + '{#OtherSetup}' + ' from the LuminaDB download page instead. Open it now?',
                          mbInformation, MB_YESNO, IDNO) = IDYES then
      ShellExecAsOriginalUser('open', '{#ReleasesUrl}', '', '', SW_SHOWNORMAL, ewNoWait, Code);
  end;
end;

function OnDownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  Result := True;
end;

procedure SupportClick(Sender: TObject);
var
  Code: Integer;
begin
  ShellExecAsOriginalUser('open', '{#SupportUrl}', '', '', SW_SHOWNORMAL, ewNoWait, Code);
end;

procedure InitializeWizard;
var
  Link: TNewStaticText;
begin
  DownloadPage := CreateDownloadPage('Getting Microsoft .NET Framework 4.8',
    'LuminaDB needs it, and this PC doesn''t have it yet. It comes straight from Microsoft.', @OnDownloadProgress);
  DownloadPage.ShowBaseNameInsteadOfUrl := True;

  { The one place the installer mentions support: a quiet link on the last page. }
  Link := TNewStaticText.Create(WizardForm);
  Link.Parent := WizardForm.FinishedPage;
  Link.Caption := 'LuminaDB is free. If it helps you, you can support it: buymeacoffee.com/awmhathif';
  Link.Font.Color := clHotLight;
  Link.Font.Style := [fsUnderline];
  Link.Cursor := crHand;
  Link.OnClick := @SupportClick;
  Link.Left := WizardForm.FinishedLabel.Left;
  Link.Top := WizardForm.FinishedPage.Height - ScaleY(40);
  Link.Width := WizardForm.FinishedLabel.Width;
  Link.WordWrap := True;
  Link.AutoSize := True;
  Link.Anchors := [akLeft, akBottom];
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo,
  MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := 'LuminaDB {#AppVersion} for ' + '{#EditionText}' + NewLine + Space + ExpandConstant('{app}') + NewLine;
#if Edition == "win7"
  if UseLegacySet then
    Result := Result + Space + 'Engines: MariaDB 10.4, PHP 8.2 and Apache (the versions that run on this Windows)' + NewLine
  else
    Result := Result + Space + 'Engines: MariaDB 10.4, PHP 8.2 and Apache, 32-bit' + NewLine;
#else
  Result := Result + Space + 'Engines: MariaDB 11.8, PHP 8.5, Caddy and Apache' + NewLine;
#endif
  if MemoTasksInfo <> '' then Result := Result + NewLine + MemoTasksInfo + NewLine;
  if not HasNet48 then
    Result := Result + NewLine + 'Microsoft .NET Framework 4.8 is needed and will be downloaded from Microsoft (about 116 MB). ' +
              'Installing it asks for administrator approval once, and Windows may need a restart afterwards.' + NewLine;
  if FileExists(ExpandConstant('{app}\LuminaDB.exe')) then
    Result := Result + NewLine + 'LuminaDB is already here: it will be updated, and your databases, projects, backups and settings stay.' + NewLine;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = wpReady) and (not HasNet48) and (not NetDownloaded) then
  begin
    DownloadPage.Clear;
    DownloadPage.Add(NetUrl, NetFile, '');
    DownloadPage.Show;
    try
      try
        DownloadPage.Download;
        NetDownloaded := True;
      except
        if DownloadPage.AbortedByUser then
          Log('.NET download cancelled')
        else
          SuppressibleMsgBox('Microsoft .NET Framework 4.8 couldn''t be downloaded: ' + AddPeriod(GetExceptionMessage) + #13#10#13#10 +
            'Check the internet connection and try again, or install .NET Framework 4.8 from microsoft.com first.', mbCriticalError, MB_OK, IDOK);
        Result := False;
      end;
    finally
      DownloadPage.Hide;
    end;
  end;
end;

{ Asks the copy of LuminaDB in this folder to close the normal way (it stops its database server cleanly). }
function CloseRunningCopy: Boolean;
var
  Exe: String;
  Code: Integer;
begin
  Result := True;
  Exe := ExpandConstant('{app}\LuminaDB.exe');
  if not FileExists(Exe) then Exit;
  if Exec(Exe, '--quit', ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, Code) then
    Result := Code = 0
  else
    Log('Could not run LuminaDB --quit: ' + SysErrorMessage(Code));
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  Result := '';
  if not CloseRunningCopy then
  begin
    Result := 'LuminaDB is still open and didn''t close. Close it (its window''s close button), then click Back and Next to try again.';
    Exit;
  end;
  if (not HasNet48) and NetDownloaded then
  begin
    WizardForm.PreparingLabel.Caption := 'Installing Microsoft .NET Framework 4.8. This can take several minutes.';
    if not ShellExec('runas', ExpandConstant('{tmp}\' + NetFile), '/passive /norestart', '', SW_SHOW, ewWaitUntilTerminated, Code) then
    begin
      Result := 'Microsoft .NET Framework 4.8 wasn''t installed (' + SysErrorMessage(Code) + '). It needs administrator approval. ' +
                'Run this installer again and choose Yes when Windows asks.';
      Exit;
    end;
    case Code of
      0: ;
      1641, 3010: RestartAfter := True;
      1602: begin Result := 'Installing Microsoft .NET Framework 4.8 was cancelled. LuminaDB needs it.'; Exit; end;
    else
      begin
        Result := 'Microsoft .NET Framework 4.8 didn''t install (error ' + IntToStr(Code) + '). Install it from microsoft.com, then run this installer again.';
        Exit;
      end;
    end;
  end;
end;

function NeedRestart: Boolean;
begin
  Result := RestartAfter;
end;

function InitializeUninstall: Boolean;
begin
  Result := CloseRunningCopy;
  if not Result then
    SuppressibleMsgBox('LuminaDB is still open and didn''t close. Close it, then uninstall again.', mbError, MB_OK, IDOK);
end;

procedure DeleteFolder(const Name: String);
begin
  if DirExists(ExpandConstant('{app}\' + Name)) then
    DelTree(ExpandConstant('{app}\' + Name), True, True, True);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Value: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    { "Start with Windows" (Settings) points at this copy: remove it with the app. }
    if RegQueryStringValue(HKCU, RunKey, 'LuminaDB', Value) and (Pos(Lowercase(ExpandConstant('{app}')), Lowercase(Value)) > 0) then
      RegDeleteValue(HKCU, RunKey, 'LuminaDB');
  end;
  if (CurUninstallStep = usPostUninstall) and not DeleteDataAsked then
  begin
    DeleteDataAsked := True;
    if DirExists(ExpandConstant('{app}\data')) or DirExists(ExpandConstant('{app}\projects')) or DirExists(ExpandConstant('{app}\backups')) then
      if SuppressibleMsgBox('LuminaDB is uninstalled. Your databases, projects and backups are still in' + #13#10 +
                            ExpandConstant('{app}') + #13#10#13#10 +
                            'Delete them too? This can''t be undone. Choose No to keep them (installing LuminaDB again finds them).',
                            mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES then
      begin
        DeleteFolder('data');
        DeleteFolder('projects');
        DeleteFolder('backups');
        DeleteFolder('snapshots');
        DeleteFolder('exports');
        DeleteFolder('mail');
        DeleteFolder('config');
        RemoveDir(ExpandConstant('{app}'));
      end;
  end;
end;
