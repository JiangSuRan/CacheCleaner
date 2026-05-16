[Setup]
AppName=C盘缓存清理工具
AppVersion=3.0
AppPublisher=CacheCleaner
DefaultDirName={autopf}\CacheCleaner
DefaultGroupName=C盘缓存清理工具
UninstallDisplayIcon={app}\CacheCleaner.exe
Compression=lzma2/ultra64
SolidCompression=yes
OutputDir=installer
OutputBaseFilename=CacheCleaner-Setup
SetupIconFile=app.ico
PrivilegesRequired=lowest

[Languages]

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加选项:"

[Files]
Source: "publish\CacheCleaner.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\C盘缓存清理工具"; Filename: "{app}\CacheCleaner.exe"; IconFilename: "{app}\CacheCleaner.exe"
Name: "{autodesktop}\C盘缓存清理工具"; Filename: "{app}\CacheCleaner.exe"; IconFilename: "{app}\CacheCleaner.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\CacheCleaner.exe"; Description: "启动 C盘缓存清理工具"; Flags: nowait postinstall skipifsilent
