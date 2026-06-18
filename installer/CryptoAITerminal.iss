; ============================================================================
;  CryptoAI Terminal — Inno Setup installer script
;  Packages the self-contained win-x64 publish folder into a single setup .exe.
;  Build with installer\build-installer.ps1 (which can also code-sign).
; ============================================================================

#define MyAppName "CryptoAI Terminal"
#define MyAppPublisher "CryptoAI"
#define MyAppExeName "CryptoAITerminal.TerminalUI.exe"

; Version + source folder can be overridden from the command line:
;   ISCC.exe /DMyAppVersion=1.6.0 /DSourceDir="...\publish" CryptoAITerminal.iss
#ifndef MyAppVersion
  #define MyAppVersion "1.6.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\CryptoAITerminal.TerminalUI\bin\Release\net8.0-windows\win-x64\publish"
#endif

[Setup]
; Stable AppId — keep this GUID forever so upgrades replace the same install.
AppId={{E2864E7B-C217-429D-A1FB-1E6EDEFE1FFB}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\CryptoAI Terminal
DefaultGroupName=CryptoAI Terminal
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
OutputDir=output
OutputBaseFilename=CryptoAITerminal-Setup-{#MyAppVersion}
SetupIconFile=..\CryptoAITerminal.TerminalUI\Assets\app.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Per-user install → no UAC/admin prompt, fewer AV/SmartScreen problems.
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
