[Setup]
AppName=LegaxyyFPS
AppVersion=1.2.1
DefaultDirName={autopf}\LegaxyyFPS
DefaultGroupName=LegaxyyFPS
UninstallDisplayIcon={app}\LegaxyyFPS.exe
Compression=lzma2
SolidCompression=yes
OutputDir=Release
OutputBaseFilename=LegaxyyFPS_Setup_v1.2.1
PrivilegesRequired=admin
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
SetupIconFile=AppIcon.ico

[Files]
Source: "dist\App\LegaxyyFPS.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "dist\App\WebView2Loader.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "dist\App\appsettings.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "dist\App\Uninstall_Overlay.bat"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "dist\App\MicrosoftEdgeWebview2Setup.exe"; DestDir: "{tmp}"; Flags: ignoreversion deleteafterinstall skipifsourcedoesntexist
Source: "dist\App\MSIAfterburnerSetup.exe"; DestDir: "{tmp}"; Flags: ignoreversion deleteafterinstall skipifsourcedoesntexist

[Icons]
Name: "{group}\LegaxyyFPS"; Filename: "{app}\LegaxyyFPS.exe"
Name: "{group}\Uninstall LegaxyyFPS"; Filename: "{uninstallexe}"
Name: "{autodesktop}\LegaxyyFPS"; Filename: "{app}\LegaxyyFPS.exe"

[Run]
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""OverlayDataBridgeStartup"" /F"; Flags: runhidden
Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/silent /install"; StatusMsg: "Memasang Microsoft Edge WebView2 Runtime (Komponen Grafis)..."; Flags: runhidden
Filename: "{tmp}\MSIAfterburnerSetup.exe"; Description: "Install MSI Afterburner & RTSS (Wajib untuk deteksi FPS)"; Flags: postinstall skipifsilent shellexec
Filename: "{app}\LegaxyyFPS.exe"; Description: "Jalankan LegaxyyFPS sekarang"; Flags: nowait postinstall shellexec
