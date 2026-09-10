#ifndef MySourceDir
  #define MySourceDir "PLAYOUT"
#endif
#ifndef MyOutputDir
  #define MyOutputDir "DIST"
#endif
#define MyAppName "SMART PLAYOUT"
#define MyAppVersion "0.6.8.50.48"
#define MyAppExeName "SMARTPlayout.exe"

[Setup]
AppId={{D74B8620-BA3B-4C99-87D8-AF88EF6D8390}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
DefaultDirName={autopf}\SMART PLAYOUT
DefaultGroupName=SMART PLAYOUT
DisableProgramGroupPage=yes
OutputDir={#MyOutputDir}
OutputBaseFilename=SMART_PLAYOUT_v0_6_8_50_48_SETUP
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
WizardStyle=modern
SetupIconFile={#MySourceDir}\SRMediaPlayer.ico
UninstallDisplayIcon={app}\SMARTPlayout.exe

[Files]
Source: "{#MySourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\SMART PLAYOUT"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\SMART PLAYOUT"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch SMART PLAYOUT"; Flags: nowait postinstall skipifsilent
