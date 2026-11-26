# 定格词岛 StillLyrics

Windows 桌面上的 Spotify 歌词岛，支持双行歌词、封面取色和微信来信提醒。

## 下载使用

在 [Releases](../../releases) 下载 `StillLyrics-WeChatEdition-1.0.6.zip`，完整解压后双击 `启动微信通知版.cmd`。首次安装未签名的 MSIX 时，Windows 会请求管理员权限。已运行词岛时，请先从托盘菜单退出旧实例。

右键词岛或托盘图标，在「微信提醒」中选择「仅任务栏闪烁」。微信最小化或收进托盘时，点击提醒卡片可以打开微信。聊天窗口在桌面可见时，词岛会继续显示歌词。闪烁事件只提供来信提醒，无法提供发送人或正文。

## 从源码构建

在 x64 Windows 上打开 PowerShell，在项目根目录运行以下命令编译并执行离线检查。

```powershell
.\build.ps1 -Check
```

生成的普通版位于 `bin\StillLyrics.exe`。运行 `.\build.ps1 -WeChatEdition -WindowCheck -FrameCheck` 可编译微信通知版并检查窗口与动画。要生成 MSIX，使用 `-MakeAppx` 指定 Windows SDK 中 `makeappx.exe` 的完整路径。
