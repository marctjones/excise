; ──────────────────────────────────────────────────────────────────────
; excise — Windows installer (Inno Setup)
;
; Built by scripts/build-windows-installer.ps1, which first runs
;   dotnet publish -c Release -r win-x64 --self-contained true
;     -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
; and then invokes iscc.exe with /DMyAppVersion=… and /DPublishDir=…
; pointing at the publish output.
;
; Targets: Windows 10 1809+ / Windows 11. Self-contained — no .NET 10
; runtime required on the target machine.
; ──────────────────────────────────────────────────────────────────────

#ifndef MyAppVersion
#define MyAppVersion "0.0.0"
#endif

#ifndef PublishDir
#error "PublishDir must be set with /DPublishDir=path-to-dotnet-publish-output"
#endif

#ifndef RepoRoot
#error "RepoRoot must be set with /DRepoRoot=path-to-repo-root"
#endif

#define MyAppName        "excise"
#define MyAppPublisher   "Marc Jones"
#define MyAppURL         "https://github.com/marctjones/excise"
#define MyAppExeName     "Excise.App.exe"
#define MyAppCliExeName  "excise.exe"
#define MyAppId          "{{C9F5B5E1-1A2D-4E5A-9C7E-6B5C4A1D2E3F}}"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
LicenseFile={#RepoRoot}\LICENSE
OutputBaseFilename=excise-{#MyAppVersion}-win-x64-setup
SetupIconFile={#RepoRoot}\Excise.App\Assets\excise_logo.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName} {#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
MinVersion=10.0.17763
; Add the Uninstall entry to Add/Remove Programs.
ChangesAssociations=yes
ChangesEnvironment=yes
; Output a single .exe at packaging\windows\Output\
OutputDir=Output

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon";   Description: "{cm:CreateDesktopIcon}";   GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "associatepdf";  Description: "Associate {#MyAppName} with .pdf files"; GroupDescription: "File associations:"; Flags: unchecked
Name: "addtopath";     Description: "Add the {#MyAppName} CLI to PATH (per-user)";   GroupDescription: "Command line:"; Flags: unchecked

[Files]
; Bring in everything dotnet publish put into the output directory.
; We deliberately use Source: "{#PublishDir}\*" with recurse so we
; never miss native sidecar files (libSkiaSharp.dll, etc.).
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}";        Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{#MyAppName} on the Web"; Filename: "{#MyAppURL}"
Name: "{group}\Uninstall {#MyAppName}";  Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}";  Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Register excise as a PDF-capable app without stealing the user's default.
; Windows 10/11 protect default handlers behind UserChoice, so the installer
; exposes excise in Open With / Default apps and the user can opt in below.
Root: HKCU; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "{#MyAppName}"; ValueData: "Software\Classes\Applications\{#MyAppExeName}\Capabilities"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyAppExeName}\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "{#MyAppName}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyAppExeName}\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "Open, inspect, edit, redact, and save PDF documents."
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyAppExeName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".pdf"; ValueData: "excise.pdf"
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: ".pdf"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyAppExeName}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""

; .pdf file association ProgID (only if user opted in via the task).
Root: HKCU; Subkey: "Software\Classes\.pdf\OpenWithProgids"; ValueType: string; ValueName: "excise.pdf"; ValueData: ""; Tasks: associatepdf; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\excise.pdf"; ValueType: string; ValueName: ""; ValueData: "PDF Document"; Tasks: associatepdf; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\excise.pdf\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"; Tasks: associatepdf
Root: HKCU; Subkey: "Software\Classes\excise.pdf\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: associatepdf

; Per-user PATH (only if user opted in via the task). System-wide PATH
; would require admin and registry HKLM, which we avoid to keep
; PrivilegesRequired=lowest.
Root: HKCU; Subkey: "Environment"; ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}"; Tasks: addtopath; Check: NeedsAddPath('{app}')

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[Code]
// Avoid double-appending the install dir to PATH if the user re-runs the
// installer. Returns False when the app dir is already a token in PATH.
// (Use // line comments, not { } — a brace comment containing {app} would be
//  closed early by that constant's own '}' and break compilation.)
function NeedsAddPath(Param: string): Boolean;
var
  OrigPath: string;
begin
  if not RegQueryStringValue(HKEY_CURRENT_USER, 'Environment', 'Path', OrigPath) then
  begin
    Result := True;
    exit;
  end;
  { Search with semicolon delimiters so 'C:\foo\excise' isn't matched by
    'C:\foo\excisescape'. }
  Result := Pos(';' + Lowercase(Param) + ';', ';' + Lowercase(OrigPath) + ';') = 0;
end;
