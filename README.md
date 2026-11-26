# 定格词岛 · StillLyrics

在 Windows 桌面顶部跟着 Spotify 看歌词。当前句、下一句和播放按钮都在同一块悬浮窗里，切歌时背景会过渡到新封面的主色。微信在后台闪烁时，词岛会暂时变成一张绿色提醒卡；点一下就能打开微信，歌词在卡片下继续走。

[下载 Windows 版](https://github.com/castlale/StillLyrics/releases/latest) · [反馈问题](https://github.com/castlale/StillLyrics/issues) · [MIT 许可证](LICENSE)

## 快速开始

发布包适用于 **Windows 11 x64，系统内部版本 22621 或更高**。歌词功能配合 Windows 版 Spotify 使用，微信提醒配合桌面微信使用。

1. 在 Releases 下载 `StillLyrics-WeChatEdition-1.0.6.zip`。如已运行旧版，先在其托盘菜单选择「退出定格词岛」。
2. 完整解压 ZIP，双击 `启动微信通知版.cmd`。首次安装未签名的 MSIX 时，Windows 会请求管理员权限。如果 PowerShell 提示下载的脚本未签名，可在核对下载来源后，打开 ZIP 的「属性」，勾选「解除锁定」，再重新解压。
3. 在 Spotify 播放一首歌。右键词岛或托盘图标可打开菜单；想先看微信提醒效果，可选「微信提醒 → 试播一条提醒」。

## 歌词与播放

| 想做什么 | 在哪里操作 |
| --- | --- |
| 看下一句或译文 | 在「歌词显示」中开启双行，或选择优先在第二行显示译文。逐字高亮需要歌词源包含逐字时间。 |
| 切歌或暂停 | 使用词岛左侧的上一首、播放／暂停、下一首按钮。右侧可显示歌曲封面。 |
| 修正错版或不同步的歌词 | 在「这首歌」中选择并记住歌词版本、导入本地 LRC 或逐字歌词，或按 0.25 秒调整时间偏移。 |
| 边工作边听歌 | 在「词岛行为」中设置鼠标移入时隐藏、全屏时隐藏，或将词岛移到合适的位置。 |

## 微信提醒

默认开启「仅任务栏闪烁」。即使 Windows 的通知应用列表里没有微信，只要桌面微信发出任务栏闪烁事件，词岛就能提示「微信请求关注」。这个事件不包含联系人和消息正文。

微信最小化、收进托盘或被其他窗口完全遮住时，词岛可以提醒。微信聊天区域在桌面上可见时会静默，包括分屏使用的情况。可见性通过屏幕上的多个点判断，若窗口只露出极窄一条边，仍可能触发提醒。

点击卡片会立即尝试打开微信，绿色同时用 **220 毫秒**从右向左收回，露出此刻的歌词。提醒自然结束也使用这段退场动画。开启 Windows 减少动画效果时，界面直接切换。

「任务栏闪烁 + 系统通知」是可选模式，需要 Windows 通知访问授权。只有微信实际发送系统通知、且用户勾选「预览联系人和摘要」时，卡片才会使用该通知提供的文字。

## 数据与歌词来源

词岛从本机 Spotify 界面或 Windows 媒体会话获取播放信息。联网匹配歌词时会向 LRCLIB、网易云音乐查询曲名和歌手，也会按歌曲 ID 查找 AMLL 的逐字歌词。匹配效果取决于具体录音版本和词源。

界面偏好、记住的歌词版本和时间偏移保存在本机的 `%LOCALAPPDATA%\StillLyrics\settings.json`。反馈问题时，通常只需提供词岛版本、Windows 版本和复现步骤；歌词匹配问题可附曲名、歌手及出错时间点。

## 从源码构建

项目使用 C#、Windows Forms 和 .NET Framework。请在 x64 Windows 上打开 PowerShell，进入仓库根目录。构建脚本使用系统的 .NET Framework 编译器和 Windows 元数据。

```powershell
.\build.ps1 -Check
.\bin\StillLyrics.exe
```

构建微信通知版并验证窗口行为及动效，可运行以下命令。窗口和帧率测试需要交互式桌面，帧率测试需要开启 Windows 动画。

```powershell
.\build.ps1 -WeChatEdition -WindowCheck -FrameCheck
```

如需自行打包 MSIX，请安装含 MakeAppx 的 Windows SDK，并通过 `-MakeAppx` 向构建脚本传入 `makeappx.exe` 的完整路径。

## 参与开发

欢迎提交 Issue 和 Pull Request。歌词匹配逻辑在 [Catalog.cs](Catalog.cs)，微信事件处理在 [IslandWeChat.cs](IslandWeChat.cs)，窗口可见性判断在 [WeChatWindow.cs](WeChatWindow.cs)。修改交互或绘制逻辑时，可在 [TimingCheck.cs](tests/TimingCheck.cs) 中补充对应场景，再运行上面的检查。

项目代码采用 [MIT 许可证](LICENSE)。歌词及专辑封面归各自权利人所有。
