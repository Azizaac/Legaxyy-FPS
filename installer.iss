[Setup]
AppName=LegaxyyFPS
AppVersion=1.0
DefaultDirName={autopf}\LegaxyyFPS
DefaultGroupName=LegaxyyFPS
UninstallDisplayIcon={app}\LegaxyyFPS.exe
Compression=lzma2
SolidCompression=yes
OutputDir=C:\Users\choir\Documents\OverlayDataBridge
OutputBaseFilename=LegaxyyFPS_Setup_Fix
PrivilegesRequired=admin
SetupIconFile=C:\Users\choir\Documents\OverlayDataBridge\AppIcon.ico

[Files]
Source: "C:\Users\choir\Documents\OverlayDataBridge\Publish\LegaxyyFPS.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "C:\Users\choir\Documents\OverlayDataBridge\Publish\appsettings.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "C:\Users\choir\Documents\OverlayDataBridge\Publish\Uninstall_Overlay.bat"; DestDir: "{app}"; Flags: ignoreversion
Source: "C:\Users\choir\Downloads\Compressed\MSIAfterburnerSetup467Beta2\MSIAfterburnerSetup467Beta2.exe"; DestDir: "{tmp}"; Flags: ignoreversion deleteafterinstall

[Icons]
Name: "{group}\LegaxyyFPS"; Filename: "{app}\LegaxyyFPS.exe"
Name: "{group}\Uninstall LegaxyyFPS"; Filename: "{uninstallexe}"
Name: "{autodesktop}\LegaxyyFPS"; Filename: "{app}\LegaxyyFPS.exe"

[Run]
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""OverlayDataBridgeStartup"" /F"; Flags: runhidden
Filename: "{tmp}\MSIAfterburnerSetup467Beta2.exe"; Description: "Install MSI Afterburner & RTSS (Wajib untuk deteksi FPS)"; Flags: postinstall skipifsilent shellexec
Filename: "{app}\LegaxyyFPS.exe"; Description: "Jalankan LegaxyyFPS sekarang"; Flags: nowait postinstall skipifsilent shellexec
