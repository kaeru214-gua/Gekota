; ============================================================
;  Gekota 安装包脚本 (Inno Setup 6)
;  发布新版本时：改 MyAppVersion，并同步改
;    Gekota.Flv\GekotaVersion.cs
;    Directory.Build.props
; ============================================================

#define MyAppName "Gekota"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Gekota Contributors"
#define MyAppExeName "Gekota.WPF.exe"

[Setup]
AppId={{7E1C0E57-8B4A-4E5F-9D6C-1A2B3C4D5E6F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\Gekota
DefaultGroupName=Gekota
DisableProgramGroupPage=yes
OutputDir=dist
OutputBaseFilename=Gekota-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=Gekota.WPF\setup_icon.ico
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#MyAppVersion}.0
VersionInfoProductVersion={#MyAppVersion}
VersionInfoProductName={#MyAppName}
VersionInfoCompany=
VersionInfoCopyright=Copyright © 2026 Gekota Contributors
VersionInfoDescription=你的哔哩哔哩录播切片小助手

[Languages]
Name: "chinesesimp"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式(&D)"; GroupDescription: "附加任务："

[Files]
Source: "Gekota.WPF\bin\Release\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Gekota"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\Gekota"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "运行 Gekota(&R)"; Flags: nowait postinstall skipifsilent