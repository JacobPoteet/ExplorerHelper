; Inno Setup script for Explorer Helper.
; Installs per-user (no admin) and registers the Explorer context-menu entries,
; which are removed again on uninstall.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

[Setup]
AppId={{B7E4C1D2-9A3F-4E8B-A6D5-2F0C7E91B384}
AppName=Explorer Helper
AppVersion={#AppVersion}
AppPublisher=Jacob Poteet
DefaultDirName={localappdata}\Programs\ExplorerHelper
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\artifacts
OutputBaseFilename=ExplorerHelper-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\ExplorerHelper.exe

[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{userprograms}\Explorer Helper"; Filename: "{app}\ExplorerHelper.exe"

[Registry]
; Right-click on a folder
Root: HKCU; Subkey: "Software\Classes\Directory\shell\ExplorerHelper"; ValueType: string; ValueName: ""; ValueData: "Clean this folder"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Directory\shell\ExplorerHelper"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\ExplorerHelper.exe"
Root: HKCU; Subkey: "Software\Classes\Directory\shell\ExplorerHelper\command"; ValueType: string; ValueName: ""; ValueData: """{app}\ExplorerHelper.exe"" ""%1"""
; Right-click on the background of an open folder
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\ExplorerHelper"; ValueType: string; ValueName: ""; ValueData: "Clean this folder"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\ExplorerHelper"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\ExplorerHelper.exe"
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\ExplorerHelper\command"; ValueType: string; ValueName: ""; ValueData: """{app}\ExplorerHelper.exe"" ""%V"""

[Run]
Filename: "{app}\ExplorerHelper.exe"; Description: "Launch Explorer Helper"; Flags: nowait postinstall skipifsilent
