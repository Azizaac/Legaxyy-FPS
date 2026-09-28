[Setup]
AppName=LegaxyyFPS
AppVersion=1.1
DefaultDirName={autopf}\LegaxyyFPS
DefaultGroupName=LegaxyyFPS
UninstallDisplayIcon={app}\LegaxyyFPS.exe
Compression=lzma2
SolidCompression=yes
OutputDir=..\Release
OutputBaseFilename=LegaxyyFPS_Setup_v1.1
PrivilegesRequired=admin
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
SetupIconFile=AppIcon.ico

[Files]
Source: "..\App\LegaxyyFPS.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\App\WebView2Loader.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\App\appsettings.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\App\index.html"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\App\Uninstall_Overlay.bat"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\App\MSIAfterburnerSetup467Beta2.exe"; DestDir: "{tmp}"; Flags: ignoreversion deleteafterinstall

[Icons]
Name: "{group}\LegaxyyFPS"; Filename: "{app}\LegaxyyFPS.exe"
Name: "{group}\Uninstall LegaxyyFPS"; Filename: "{uninstallexe}"
Name: "{autodesktop}\LegaxyyFPS"; Filename: "{app}\LegaxyyFPS.exe"

[Run]
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""OverlayDataBridgeStartup"" /F"; Flags: runhidden
Filename: "{tmp}\MSIAfterburnerSetup467Beta2.exe"; Description: "Install MSI Afterburner & RTSS (Wajib untuk deteksi FPS)"; Flags: postinstall skipifsilent shellexec
Filename: "{app}\LegaxyyFPS.exe"; Description: "Jalankan LegaxyyFPS sekarang"; Flags: nowait postinstall skipifsilent shellexec
