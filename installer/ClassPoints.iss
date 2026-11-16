; 班级积分 · Windows 安装程序（Inno Setup 6）
;
; 设计取舍：
;   * 装到 Program Files，机器级安装（PrivilegesRequired=admin）。
;     代价是每次升级要过一次 UAC，好处是"任何账号登录都能用"，
;     走廊那台查询机换了账号也不用重装。
;   * 用户数据**不在安装目录被 [Files] 管着的那些文件里**：装在非系统盘时是
;     「安装目录\data」，装在 C 盘时是「%LOCALAPPDATA%\ClassPoints」。
;     两种情况都覆盖升级、卸载重装不碰名单和分数。这一点由程序自己保证，
;     安装程序这边刻意不写任何删除 data 或 {localappdata} 的规则。
;   * 静默升级靠 /VERYSILENT + /CLOSEAPPLICATIONS：
;     程序自己拉起安装包后立即退出，安装程序用 Restart Manager 关掉残留进程再换文件。
;
; 手工编译：
;   ISCC.exe /DMyVersion=1.2.0 installer\ClassPoints.iss
; 正常走 scripts\pack-installer.ps1，参数由脚本传。

#ifndef MyVersion
  #define MyVersion "1.2.0"
#endif

#ifndef MySourceDir
  #define MySourceDir "..\dist\ClassPoints-win-x64"
#endif

#ifndef MyOutputDir
  #define MyOutputDir "..\dist"
#endif

#ifndef MyIconFile
  #define MyIconFile "..\src\ClassPoints.App\Assets\app.ico"
#endif

#ifndef LangFile
  #define LangFile "lang\ChineseSimplified.isl"
#endif

#define MyAppName "班级积分"
#define MyAppExeName "ClassPoints.exe"
; AppId 一旦发布就不能改：它是"这是同一个软件"的唯一凭据，
; 改了之后升级会变成并存两份，控制面板里出现两条。
#define MyAppId "{{B4A0F1C2-7E3D-4A86-9C15-6F2D8B1E4A73}"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyVersion}
AppVerName={#MyAppName} {#MyVersion}
AppPublisher={#MyAppName}
VersionInfoVersion={#MyVersion}.0
DefaultDirName={autopf}\ClassPoints
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; 教室一体机最低是 Win10 1809（WinUI 3 的下限），低于这个直接拦住，
; 总比装完双击报 XamlParseException 强。
MinVersion=10.0.17763
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; 强制管理员：写 Program Files、写 HKLM 卸载项都需要
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=
; 中文向导。语言包随仓库携带 —— Inno 官方安装包并不保证带简体中文，
; 缺了就会静默退回英文，老师装软件时看到英文界面容易点错。
ShowLanguageDialog=no
OutputDir={#MyOutputDir}
OutputBaseFilename=ClassPoints-Setup-{#MyVersion}
SetupIconFile={#MyIconFile}
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName} {#MyVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
DisableWelcomePage=no
; 升级时自动关掉正在跑的程序；不自动重启，重启由 [Run] 负责，
; 两套机制同时用会出现"装完开了两个窗口"。
CloseApplications=yes
RestartApplications=no
UsePreviousAppDir=yes
; 安装日志留在 %TEMP%，出问题让老师直接发过来
SetupLogging=yes

[Languages]
Name: "chs"; MessagesFile: "{#LangFile}"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务:"; Flags: unchecked

[Files]
; ignoreversion：版本号相同时也要覆盖（同一个版本重装、以及 CI 重打的情况）
Source: "{#MySourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; 装完就启动。silent 升级时这条也会执行 —— 老师点完「更新」，
; 屏幕闪一下又回到原来的界面，不用自己去开始菜单找。
Filename: "{app}\{#MyAppExeName}"; Description: "启动 {#MyAppName}"; Flags: nowait

[UninstallDelete]
; 只清自己在安装目录里产生的临时文件。用户数据（数据库、备份、同步凭据）
; 要么在「安装目录\data」，要么在 %LOCALAPPDATA%\ClassPoints，卸载一律不碰 ——
; data 目录不在这里登记，也就不会被卸载程序清掉。
Type: files; Name: "{app}\启动.cmd"

[Code]
// 装之前先在界面上把"数据在哪、卸不掉"说清楚。
// 老师在控制面板里点卸载时最怕的就是"名单会不会没了"，写在说明里比写在文档里有效。
//
// 数据位置随安装位置变（程序自己判断）：
//   装在非系统盘 → 数据在「安装目录\data」，卸载也留着；
//   装在 C 盘   → 数据在 %LOCALAPPDATA%\ClassPoints，卸载同样不碰。
procedure InitializeWizard();
begin
  WizardForm.WelcomeLabel2.Caption :=
    WizardForm.WelcomeLabel2.Caption + #13#10#13#10 +
    '班级名单、积分记录、备份都在随程序的数据目录里（装在非系统盘时就在安装目录下的 data 文件夹），' +
    '升级或卸载都不会删除。装在 C 盘时它们位于当前用户的 AppData 目录，' +
    '也可以在「设置 → 本机数据」里一键搬到别的盘。';
end;

// USB 读卡器走 CH340 转串口，这颗芯片的驱动不随 Windows 分发。
// 机器上插着它（枚举表里有 VID_1A86&PID_7523）但驱动没装时，装完顺手把驱动装上：
// 安装程序本身就是提权的，不再弹 UAC；设备不在就什么都不做，不给没读卡器的老师装东西。
// 装了之后驱动会留在系统驱动仓库里，卸载本程序不回收 —— 别的程序可能也在用它。
//
// 【写法约束】整段 Pascal 里**不能出现以 [ 开头的行**：
// Inno 的行式分区解析把"行首是 [ "当成新的 section 头，而 Format 的数组参数
// 换行后正好长这样 —— 于是编译直接报 "Invalid section tag."（v1.3 发布就是这么挂的）。
// 参数多的调用先拼成字符串变量，或者让数组和函数留在同一行。
procedure CurStepChanged(CurStep: TSetupStep);
var
  Rc: Integer;
  Params: String;
begin
  if CurStep <> ssPostInstall then Exit;
  if not RegKeyExists(HKEY_LOCAL_MACHINE,
      'SYSTEM\CurrentControlSet\Enum\USB\VID_1A86&PID_7523') then Exit;

  Params := Format('/C pnputil /add-driver "%s\drivers\CH341SER\ch341ser.inf" /install', [ExpandConstant('{app}')])
    + ' & pnputil /scan-devices';
  Exec(ExpandConstant('{cmd}'), Params, '', SW_HIDE, ewWaitUntilTerminated, Rc);
  if Rc = 0 then
    Log('CH340 driver installed for existing device')
  else
    Log(Format('CH340 driver install failed (exit %d); the app offers a manual button', [Rc]));
end;
