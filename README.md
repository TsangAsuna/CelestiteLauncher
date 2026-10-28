# Celestite [![GitHub Release](https://img.shields.io/github/v/release/Kengxxiao/CelestiteLauncher)](https://github.com/Kengxxiao/CelestiteLauncher/releases)
![GitHub License](https://img.shields.io/github/license/Kengxxiao/CelestiteLauncher)
![GitHub last commit](https://img.shields.io/github/last-commit/Kengxxiao/CelestiteLauncher)
[![GitHub Downloads](https://img.shields.io/github/downloads/Kengxxiao/CelestiteLauncher/total)](https://github.com/Kengxxiao/CelestiteLauncher/releases)
[![Build Status](https://dev.azure.com/Kengxxiao/Celestite-opensource/_apis/build/status%2FKengxxiao.CelestiteLauncher?branchName=main)](https://dev.azure.com/Kengxxiao/Celestite-opensource/_build/latest?definitionId=5&branchName=main)

**Celestite** 是一款跨平台的第三方 **DMMGamePlayer** 实现，提供了基础的启动器功能和一些额外的高级特性，包括但不限于：  
* 在日本以外的国家/地区使用 **DMMGamePlayer** 的功能[^1]。
* 自定义游戏启动命令行。
* 在非 Windows 系统上使用 Proton/Wine 等 Windows 兼容层启动游戏。
* 通过模拟启动流程完成 DMM POINT GET MISSION 的任务。
* 以无 GUI 模式执行游戏启动流程。
* (Windows) 在基于 WebView2 的内置浏览器上以账号隔离的方式运行浏览器游戏。
  
[^1]: 该项功能不包含在开源代码中，需要在 [Releases](https://github.com/Kengxxiao/CelestiteLauncher/releases) 中下载以获得该项功能支持。

同时，**Celestite** 兼容 **DMMGamePlayer** 的数据格式，允许用户执行以下操作：
* 在 **Celestite** 上直接使用 **DMMGamePlayer** 的登录态。
* 共享 **DMMGamePlayer** 中已经下载完成的游戏。

## 使用
要使用 **Celestite**，建议您在 [Releases](https://github.com/Kengxxiao/CelestiteLauncher/releases) 中下载对应系统的最新版本。  
由于启动器本体不包含更新检查功能，因此建议您在 Github 上定期检查是否有针对 BUG 的软件更新。

## 高级启动
可以使用一些特定的命令行参数使 **Celestite** 自动执行某些功能。
* ```--nogui``` 启用无 GUI 模式，该模式在运行完成后自动退出。
* ```--username <username> --password <password>``` 在无 GUI 模式下定义要登录的 DMM 用户名和密码，如果不使用那么 **Celestite** 会使用上次在 GUI 模式下已经成功登录的账号（使用无 GUI 模式登录的账号不会被保存）。
* ```--productId <productId> --type <type>``` 要求 **Celestite** 直接启动某个游戏。如果 **Celestite** 未在后台运行，那么启动指令将会在登录成功后开始执行；反之，启动指令将会被传递给正在运行的 **Celestite** 实例执行。
### 示例
* ```Celestite.Desktop.exe --nogui --username example@example.com --password example --productId mementomori --type GCL``` 代表在无 GUI 模式下使用邮箱为 example@example.com 的账号登录 DMM 后启动本地的 MementoMori 游戏。

---

## 本 fork 中已确认 / 可配置的功能（对应 issue：自定义下载目录、去日本 IP 限制、无需 tun、BepInEx）

### 1. DMM 下载目录可自定义（不必装在 C 盘）
- UI：设置页「默认游戏安装文件夹」→ 选中任意文件夹 (`Celestite/Pages/SettingsPage.axaml:213-221`, `Celestite/ViewModels/Pages/SettingsViewModel.cs:120-138`)
- 后端：`ConfigUtils.UpdateDmmGamesDefaultInstallFolder` (`Celestite/Utils/ConfigUtils.cs:62-68`) 写入 `dmmgame.cnf` 的 `DefaultInstallDir`
- 读取：`DmmGamesDefaultInstallFolder` (`ConfigUtils.cs:39`)；下载入口 `KashimaDownloadManager.PushDownloadInfo` (`Celestite/Network/Downloader/KashimaDownloadManager.cs:139`) 以它为 `baseInstallDir`
- 结论：**已支持**。将下载目录设到 D 盘或任意文件夹后，游戏不会再默认写入 C 盘。

### 2. 游戏主程序目录可自定义到任意文件夹
- `GameSettingsDialogViewModel.UseGameFilesOnDisk` (`Celestite/ViewModels/Dialogs/GameSettingsDialogViewModel.cs:138-189`): 选择已安装游戏的主程序所在任意文件夹，并写入 `content.Detail.Path`
- 结合上述自定义下载目录，游戏主程序可放在任意文件夹
- 结论：**已支持**。

### 3. 不需要代理 / tun 模式启动游戏
- 客户端代码中**不存在**日本 IP 限制逻辑，也不强制 tun/代理：
  - `DynamicProxyImpl.GetProxy` (`Celestite/Network/DynamicProxy/DynamicProxy.cs:10-16`)：Windows 下优先用系统代理，未配置代理时回退 `HttpNoProxy`（直连）
  - `HttpClient.DefaultProxy = DynamicProxyImpl.GetProxy()` (`Celestite/Network/HttpHelper.cs:264`)
- 区域 / IP 限制是 **DMM 服务器端** 按客户端公网 IP 判断的（见 `Network.Models.DmmGamePlayerApiResponse` 的 `COUNTRY_NOT_ALLOWED = 801` / `AREA_NOT_ALLOWED = 803`），**不在本启动器客户端代码中**，因此**无法在客户端“移除”**。
- 如需在日本以外地区游玩，仍需自备日本 IP/代理；本启动器支持可选的系统代理 / Himari 代理（设置页开关），但**不强制 tun**。
- 结论：本启动器本身可直连启动，无需 tun；服务器端区域限制无法通过客户端改动去除（开源版不含服务器端绕过逻辑，见 README 脚注 [^1]，该功能仅在二进制 Releases 提供）。

### 4. BepInEx 插件支持
- 启动器通过 `ProcessUtils.LaunchProcess` (`Celestite/Utils/ProcessUtils.cs:53-65`) 直接启动游戏主程序，不对游戏目录做完整性覆盖
- 操作：将 BepInEx 解压到游戏主程序目录即可
- 建议：在对应游戏的设置中开启「强制跳过文件校验」（`ForceSkipFileCheck`, `Celestite/ViewModels/Dialogs/GameSettingsDialog.axaml` 与 `GameSettingsDialogViewModel.cs:33-60`），防止启动器文件校验把 BepInEx 文件还原
- 结论：**已支持**（架构层面即可用，无需额外代码）。

### 不能在客户端完成的
- 移除日本 IP / 区域限制：由 DMM 服务器端强制，客户端无法绕过；需日本 IP 或代理。
