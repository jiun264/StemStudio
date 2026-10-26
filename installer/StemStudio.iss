#define AppName "StemStudio"
#define AppVersion "0.1.0"
#define AppPublisher "StemStudio"
#define PayloadDir "..\artifacts\installer\payload"

[Setup]
AppId={{8BE4E2D0-0C85-4F9D-AD50-0B637D152624}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\Programs\StemStudio
DefaultGroupName=StemStudio
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\artifacts\installer
OutputBaseFilename=StemStudio-Setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\StemStudio.App.exe
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "建立桌面捷徑"; GroupDescription: "其他工作："; Flags: unchecked

[Files]
Source: "{#PayloadDir}\StemStudio.App.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\StemStudio.Worker.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\python\*"; DestDir: "{app}\python"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PayloadDir}\backend\demucs_worker.py"; DestDir: "{app}\backend"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\StemStudio"; Filename: "{app}\StemStudio.App.exe"
Name: "{autodesktop}\StemStudio"; Filename: "{app}\StemStudio.App.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\StemStudio.App.exe"; Description: "啟動 StemStudio"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
