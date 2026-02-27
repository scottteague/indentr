; Indentr Installer
; Requires Inno Setup 6.1 or later: https://jrsoftware.org/isdl.php
;
; Before compiling this script, run build.bat (or manually run dotnet publish)
; so that the publish output folder exists.

#define MyAppName      "Indentr"
; MyAppVersion can be overridden at compile time:
;   ISCC.exe /DMyAppVersion=1.0 indentr.iss
; If not provided (e.g. local build), the default below is used.
#ifndef MyAppVersion
  #define MyAppVersion "0.001"
#endif
#define MyAppPublisher "Indentr Contributors"
#define MyAppURL       "https://github.com/scottteague/indentr"
#define MyAppExeName   "Indentr.UI.exe"
#define MyAppIcon      "..\Indentr.UI\img\Organiz.ico"

; Path to the self-contained publish output produced by build.bat / dotnet publish.
#define PublishDir     "..\Indentr.UI\bin\Release\net10.0\win-x64\publish"

; PostgreSQL: versions to search for on an existing install (newest first).
#define PgMinVersion   14
#define PgMaxVersion   17

; EDB installer URL — update the patch version number when a new release ships.
; Check https://www.enterprisedb.com/downloads/postgres-postgresql-downloads for the latest.
#define PgDownloadUrl  "https://get.enterprisedb.com/postgresql/postgresql-17.4-1-windows-x64.exe"

#define PgDefaultPort  "5432"
#define PgDbName       "indentr"

; ── Setup ────────────────────────────────────────────────────────────────────

[Setup]
; AppId uniquely identifies this application for Add/Remove Programs.
; Do NOT change this value after the first public release.
; {{ is Inno Setup's escape for a literal {, so this produces {8de927b9-...}
AppId={{8de927b9-a7a0-4588-a6bd-fc37a6d3c91a}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
OutputDir=output
OutputBaseFilename=IndentrSetup-{#MyAppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
SetupIconFile={#MyAppIcon}
; Admin rights needed to install PostgreSQL if required.
PrivilegesRequired=admin
DisableProgramGroupPage=yes
MinVersion=6.1

; ── Languages ────────────────────────────────────────────────────────────────

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

; ── Tasks ────────────────────────────────────────────────────────────────────

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked

; ── Files ────────────────────────────────────────────────────────────────────

[Files]
; Self-contained publish output (includes the .NET runtime).
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

; ── Icons ────────────────────────────────────────────────────────────────────

[Icons]
Name: "{group}\{#MyAppName}";            Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}";  Filename: "{uninstallexe}"
Name: "{commondesktop}\{#MyAppName}";    Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

; ── Run after install ────────────────────────────────────────────────────────

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

; ── Code ─────────────────────────────────────────────────────────────────────

[Code]

// ── Globals ──────────────────────────────────────────────────────────────────

var
  PsqlPath:          String;   // full path to psql.exe once found / after install
  PgNeedsInstall:    Boolean;  // true = PostgreSQL not found on this machine

  // Custom wizard pages
  PgSetupPage:       TInputQueryWizardPage;  // shown when PG already installed
  PgInstallPage:     TInputQueryWizardPage;  // shown when PG needs to be installed

// ── PostgreSQL detection ──────────────────────────────────────────────────────

// Returns the full path to psql.exe if a supported PostgreSQL version is found,
// or an empty string if not.
function FindPsqlExe: String;
var
  V:    Integer;
  Path: String;
begin
  Result := '';
  for V := {#PgMaxVersion} downto {#PgMinVersion} do
  begin
    Path := 'C:\Program Files\PostgreSQL\' + IntToStr(V) + '\bin\psql.exe';
    if FileExists(Path) then
    begin
      Result := Path;
      Exit;
    end;
  end;
end;

// ── Custom wizard pages ───────────────────────────────────────────────────────

procedure InitializeWizard;
begin
  PsqlPath       := FindPsqlExe;
  PgNeedsInstall := (PsqlPath = '');

  if PgNeedsInstall then
  begin
    // PostgreSQL not found: we will download and install it.
    // Collect the password the user wants to assign to the postgres superuser.
    PgInstallPage := CreateInputQueryPage(
      wpSelectDir,
      'PostgreSQL Not Found',
      'PostgreSQL {#PgMinVersion}+ was not found on this computer.',
      'The installer will download and install PostgreSQL {#PgMaxVersion} (~330 MB).' + #13#10 +
      'An internet connection is required.' + #13#10#13#10 +
      'Choose a password for the PostgreSQL ''postgres'' superuser.' + #13#10 +
      'Leave blank to use trust authentication (no password — suitable for a single-user machine).');
    PgInstallPage.Add('Password (leave blank for no password):', True);
    PgInstallPage.Add('Confirm password:', True);
  end
  else
  begin
    // PostgreSQL found: ask for the existing superuser password to create the DB.
    PgSetupPage := CreateInputQueryPage(
      wpSelectDir,
      'PostgreSQL Database Setup',
      'Create the Indentr database',
      'PostgreSQL was found at:' + #13#10 +
      '  ' + ExtractFileDir(PsqlPath) + #13#10#13#10 +
      'Enter the ''postgres'' superuser password to create the ' +
      '''indentr'' database.' + #13#10 +
      'Leave blank if your installation uses trust authentication (no password).' + #13#10 +
      'If the database already exists this step is harmless.');
    PgSetupPage.Add('Superuser password (leave blank for no password):', True);
  end;
end;

// Validate the password confirmation field on the install page.
function NextButtonClick(CurPageID: Integer): Boolean;
var
  Pass, Confirm: String;
begin
  Result := True;

  if PgNeedsInstall and (PgInstallPage <> nil) and
     (CurPageID = PgInstallPage.ID) then
  begin
    Pass    := PgInstallPage.Values[0];
    Confirm := PgInstallPage.Values[1];
    if Pass <> Confirm then
    begin
      MsgBox('The passwords do not match. Please try again.', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

// ── Helpers ───────────────────────────────────────────────────────────────────

// Escapes a string for safe embedding inside a JSON double-quoted value.
function JsonEscape(S: String): String;
var
  I: Integer;
  C: Char;
begin
  Result := '';
  for I := 1 to Length(S) do
  begin
    C := S[I];
    if      C = '"'  then Result := Result + '\"'
    else if C = '\'  then Result := Result + '\\'
    else if C = #8   then Result := Result + '\b'
    else if C = #9   then Result := Result + '\t'
    else if C = #10  then Result := Result + '\n'
    else if C = #12  then Result := Result + '\f'
    else if C = #13  then Result := Result + '\r'
    else Result := Result + C;
  end;
end;

// Writes a ready-to-use config.json for the installing user so the app opens
// directly without showing the profile picker on first launch.
// Skipped if a config already exists (e.g. reinstall or upgrade).
procedure WriteDefaultConfig(Password: String);
var
  ConfigDir:  String;
  ConfigFile: String;
  Json:       String;
begin
  ConfigDir  := ExpandConstant('{%USERPROFILE}') + '\.config\indentr';
  ConfigFile := ConfigDir + '\config.json';

  if FileExists(ConfigFile) then Exit;

  Json :=
    '{' + #13#10 +
    '  "LastProfile": "Local",' + #13#10 +
    '  "Profiles": [' + #13#10 +
    '    {' + #13#10 +
    '      "Name": "Local",' + #13#10 +
    '      "Username": "' + JsonEscape(ExpandConstant('{username}')) + '",' + #13#10 +
    '      "LocalSchemaId": "",' + #13#10 +
    '      "Database": {' + #13#10 +
    '        "Host": "localhost",' + #13#10 +
    '        "Port": 5432,' + #13#10 +
    '        "Name": "indentr",' + #13#10 +
    '        "Username": "postgres",' + #13#10 +
    '        "Password": "' + JsonEscape(Password) + '"' + #13#10 +
    '      }' + #13#10 +
    '    }' + #13#10 +
    '  ]' + #13#10 +
    '}';

  ForceDirectories(ConfigDir);
  SaveStringToFile(ConfigFile, Json, False);
end;

// Returns the postgres superuser password the user entered (may be empty).
function GetPgPassword: String;
begin
  if PgNeedsInstall then
    Result := PgInstallPage.Values[0]
  else
    Result := PgSetupPage.Values[0];
end;

// Runs psql to create the indentr database.
// The database is created only if it does not already exist.
// Returns an empty string on success, or a brief error description on failure.
function CreateIndentrDatabase(PsqlExe, Password: String): String;
var
  SqlFile:    String;
  CmdParams:  String;
  ResultCode: Integer;
  PgEnv:      String;
begin
  Result := '';

  // Write an idempotent CREATE DATABASE statement to a temp file.
  // \gexec pipes the SELECT output back to psql as a command; when the
  // database already exists the SELECT returns no rows, so nothing runs.
  SqlFile := ExpandConstant('{tmp}\indentr_create_db.sql');
  if not SaveStringToFile(SqlFile,
    'SELECT ''CREATE DATABASE {#PgDbName}''' + #13#10 +
    'WHERE NOT EXISTS (' + #13#10 +
    '  SELECT FROM pg_database WHERE datname = ''{#PgDbName}''' + #13#10 +
    ');' + #13#10 +
    '\gexec' + #13#10,
    False) then
  begin
    Result := 'Could not write temporary SQL file to ' + SqlFile;
    Exit;
  end;

  // Set PGPASSWORD via cmd.exe so we never put the password on the command line.
  if Password <> '' then
    PgEnv := 'set "PGPASSWORD=' + Password + '" && '
  else
    PgEnv := '';

  CmdParams := '/C ' + PgEnv +
    '"' + PsqlExe + '" -U postgres -p {#PgDefaultPort} -f "' + SqlFile + '"';

  if not Exec(ExpandConstant('{sys}\cmd.exe'),
              CmdParams, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Result := 'Could not launch cmd.exe to run psql'
  else if ResultCode <> 0 then
    Result := 'psql exited with code ' + IntToStr(ResultCode) +
              '. Check that PostgreSQL is running and the password is correct.';
end;

// ── Post-install: PostgreSQL setup and database creation ──────────────────────

procedure CurStepChanged(CurStep: TSetupStep);
var
  PgInstaller:  String;
  InstallArgs:  String;
  Password:     String;
  ResultCode:   Integer;
  ErrMsg:       String;
begin
  if CurStep <> ssPostInstall then Exit;

  Password := GetPgPassword;

  // ── Step 1: Install PostgreSQL if not present ────────────────────────────

  if PgNeedsInstall then
  begin
    WizardForm.StatusLabel.Caption := 'Downloading PostgreSQL {#PgMaxVersion}...';
    WizardForm.FilenameLabel.Caption := '{#PgDownloadUrl}';

    try
      DownloadTemporaryFile('{#PgDownloadUrl}', 'postgresql-setup.exe', '', nil);
    except
      MsgBox(
        'Failed to download the PostgreSQL installer.' + #13#10#13#10 +
        'Please install PostgreSQL {#PgMinVersion}+ manually:' + #13#10 +
        '  https://www.postgresql.org/download/windows/' + #13#10#13#10 +
        'Then create a database named ''{#PgDbName}'' and launch Indentr.',
        mbError, MB_OK);
      Exit;
    end;

    WizardForm.StatusLabel.Caption  := 'Installing PostgreSQL {#PgMaxVersion}...';
    WizardForm.FilenameLabel.Caption := 'This may take a minute or two.';

    PgInstaller := ExpandConstant('{tmp}\postgresql-setup.exe');

    // Build silent install arguments.
    // pgAdmin and Stack Builder are excluded to keep the install lean.
    if Password <> '' then
      InstallArgs :=
        '--mode unattended' +
        ' --unattendedmodeui minimal' +
        ' --superpassword "' + Password + '"' +
        ' --servicename postgresql-{#PgMaxVersion}' +
        ' --servicepassword "' + Password + '"' +
        ' --serverport {#PgDefaultPort}' +
        ' --enable-components server,commandlinetools' +
        ' --disable-components pgAdmin,stackbuilder'
    else
      // Trust auth: still need *some* password for the EDB installer's --superpassword
      // flag, but we will configure pg_hba.conf for trust after install.
      // Use a throwaway value; the user will connect without a password.
      InstallArgs :=
        '--mode unattended' +
        ' --unattendedmodeui minimal' +
        ' --superpassword "indentr_setup_tmp"' +
        ' --servicename postgresql-{#PgMaxVersion}' +
        ' --servicepassword "indentr_setup_tmp"' +
        ' --serverport {#PgDefaultPort}' +
        ' --enable-components server,commandlinetools' +
        ' --disable-components pgAdmin,stackbuilder';

    if not Exec(PgInstaller, InstallArgs, '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
    begin
      MsgBox('Could not launch the PostgreSQL installer.', mbError, MB_OK);
      Exit;
    end;

    if ResultCode <> 0 then
    begin
      MsgBox(
        'PostgreSQL installation returned exit code ' + IntToStr(ResultCode) + '.' + #13#10 +
        'Please install PostgreSQL manually and re-run this installer.',
        mbError, MB_OK);
      Exit;
    end;

    // Give the Windows service a moment to start before we connect.
    WizardForm.StatusLabel.Caption  := 'Waiting for PostgreSQL service...';
    WizardForm.FilenameLabel.Caption := '';
    Sleep(4000);

    // Locate psql in the freshly installed location.
    PsqlPath := FindPsqlExe;
    if PsqlPath = '' then
      PsqlPath := 'C:\Program Files\PostgreSQL\{#PgMaxVersion}\bin\psql.exe';

    // When trust auth was requested, reconfigure pg_hba.conf.
    if Password = '' then
    begin
      WizardForm.StatusLabel.Caption := 'Configuring PostgreSQL authentication...';
      // Replace the default pg_hba.conf with an all-trust local config.
      // This mirrors the docker-compose trust setup so the app works with
      // no password out of the box.
      SaveStringToFile(
        'C:\Program Files\PostgreSQL\{#PgMaxVersion}\data\pg_hba.conf',
        '# TYPE  DATABASE        USER            ADDRESS                 METHOD' + #13#10 +
        'local   all             all                                     trust'  + #13#10 +
        'host    all             all             127.0.0.1/32            trust'  + #13#10 +
        'host    all             all             ::1/128                 trust'  + #13#10,
        False);
      // Reload PostgreSQL configuration.
      Exec(ExpandConstant('{sys}\cmd.exe'),
           '/C "C:\Program Files\PostgreSQL\{#PgMaxVersion}\bin\pg_ctl.exe"' +
           ' reload -D "C:\Program Files\PostgreSQL\{#PgMaxVersion}\data"',
           '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      Sleep(1000);
    end;
  end;

  // ── Step 2: Create the indentr database ──────────────────────────────────

  WizardForm.StatusLabel.Caption  := 'Creating the Indentr database...';
  WizardForm.FilenameLabel.Caption := '';

  ErrMsg := CreateIndentrDatabase(PsqlPath, Password);

  if ErrMsg <> '' then
    MsgBox(
      'The Indentr database could not be created automatically.' + #13#10#13#10 +
      'Reason: ' + ErrMsg + #13#10#13#10 +
      'You can create it manually by running:' + #13#10 +
      '  psql -U postgres -c "CREATE DATABASE {#PgDbName};"' + #13#10#13#10 +
      'Indentr will prompt you for connection details on first launch.',
      mbInformation, MB_OK);

  // ── Step 3: Write config.json so the app opens without a profile picker ───

  WizardForm.StatusLabel.Caption  := 'Writing configuration...';
  WizardForm.FilenameLabel.Caption := '';
  WriteDefaultConfig(Password);
end;
