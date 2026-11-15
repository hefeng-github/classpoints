CH340 / CH341 USB 转串口驱动（WCH 南京沁恒）

来源：https://www.wch.cn/downloads/CH341SER_EXE.html
取自本机 DriverStore 里由官方安装包释放的驱动包（WHQL 签名，CH341SER.CAT 原样保留）。

用途：USB 读卡器（CH340 串口方案）在没装驱动的电脑上不出现 COM 口。
程序在「设置 → 读卡器」发现"有 CH340 设备但没有驱动"时会提供一键安装
（pnputil /add-driver，弹一次 UAC 授权）；安装包在检测到该设备时也会自动装。

文件清单：
  ch341ser.inf      驱动信息文件
  CH341SER.CAT      目录签名文件（不要改，改了签名失效）
  CH341S64.SYS      x64 内核驱动
  CH341PORTSA64.DLL / CH341PT.DLL / CH341PTA64.DLL   共安装器组件
