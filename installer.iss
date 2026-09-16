; LuaShareX installer — per-user, no admin rights required.
;
; Built by CI (.github/workflows/release.yml) or locally:
;   dotnet publish LuaShareX.csproj -c Release -r win-x64 --self-contained false -o publish
;   ISCC.exe /DAppVersion=1.0.0 installer.iss
;
; Installs to %LocalAppData%\Programs\LuaShareX so the in-app updater
; (GitHub Releases) can overwrite files without elevation.

#define MyAppName "LuaShareX"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define MyAppPublisher "manifest-dex"
#define MyAppURL "https://github.com/manifest-dex/LuaShareX"
#define MyAppExeName "LuaShareX.exe"

[Setup]
AppId={{0E741477-195B-42B0-A8AF-1AD59DDCB8B8}
AppName={#MyAppName}
AppVersion={#AppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=dist
OutputBaseFilename=LuaShareX-Setup-v{#AppVersion}-win-x64
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
CloseApplicationsFilter=LuaShareX.exe
RestartApplications=no
UninstallDisplayName={#MyAppName}
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
