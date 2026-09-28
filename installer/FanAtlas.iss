#ifndef AppSource
  #define AppSource "..\dist\FanAtlas"
#endif
#ifndef OutputPath
  #define OutputPath "..\dist"
#endif
[Setup]
AppId={{9CE73370-9B32-4FAF-82CA-C66A350629ED}
AppName=Crazy_Batto FanAtlas
AppVersion=0.2.0
AppPublisher=Crazy_Batto
AppPublisherURL=https://github.com/dannymeienbrock99-prog/L-fter
DefaultDirName={localappdata}\Programs\CrazyBatto\FanAtlas
DefaultGroupName=Crazy_Batto FanAtlas
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputPath}
OutputBaseFilename=CrazyBatto-FanAtlas-Setup-0.2.0
SetupIconFile=..\assets\fanatlas.ico
UninstallDisplayIcon={app}\FanAtlas.exe
WizardStyle=modern
WizardImageFile=..\assets\installer-wizard.png
WizardSmallImageFile=..\assets\fan.png
WizardImageStretch=yes
DisableWelcomePage=no
DisableProgramGroupPage=yes
Compression=lzma2
SolidCompression=yes
CloseApplications=no
RestartApplications=no
AppMutex=Local\CrazyBatto-FanAtlas-v2
UninstallDisplayName=Crazy_Batto FanAtlas
VersionInfoDescription=Crazy_Batto FanAtlas Installation
[Languages]
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
[Tasks]
Name: "desktopicon"; Description: "Verknüpfung auf dem Desktop"; GroupDescription: "Verknüpfungen:"; Flags: unchecked
[Files]
Source: "{#AppSource}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{group}\FanAtlas"; Filename: "{app}\FanAtlas.exe"
Name: "{group}\Kurzanleitung"; Filename: "{app}\ANLEITUNG.html"
Name: "{autodesktop}\FanAtlas"; Filename: "{app}\FanAtlas.exe"; Tasks: desktopicon
[Run]
Filename: "{app}\FanAtlas.exe"; Description: "FanAtlas starten"; Flags: nowait postinstall skipifsilent
Filename: "{app}\Extras\de.crazybatto.fanatlas.streamDeckPlugin"; Description: "Stream-Deck-Plugin zur Installation öffnen"; Flags: shellexec nowait postinstall skipifsilent unchecked
; Persönliche Einstellungen in LocalAppData\CrazyBatto\FanAtlas bleiben bei Deinstallation erhalten.

