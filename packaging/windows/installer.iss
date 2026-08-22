#ifndef AppVersion
#define AppVersion "0.1.0"
#endif

[Setup]
AppId={{B699E147-A5D9-4E73-AB0F-D207AFD64E12}
AppName=TSM Desktop
AppVersion={#AppVersion}
AppPublisher=Wuild
AppPublisherURL=https://github.com/Wuild/TSM-App
AppSupportURL=https://github.com/Wuild/TSM-App/issues
AppUpdatesURL=https://github.com/Wuild/TSM-App/releases
SourceDir=..\..
DefaultDirName={localappdata}\Programs\TSM Desktop
DefaultGroupName=TSM Desktop
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=artifacts\packages
OutputBaseFilename=TSM-{#AppVersion}-windows-x86_64-setup
SetupIconFile=TSM\Assets\tsm-app.ico
UninstallDisplayIcon={app}\app\TSM.exe
LicenseFile=LICENSE
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "artifacts\publish\win-x64\*"; DestDir: "{app}\app"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\TSM Desktop"; Filename: "{app}\app\TSM.exe"
Name: "{autodesktop}\TSM Desktop"; Filename: "{app}\app\TSM.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\app\TSM.exe"; Description: "Launch TSM Desktop"; Flags: nowait postinstall skipifsilent
