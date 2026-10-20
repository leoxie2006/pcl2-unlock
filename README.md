# pcl2-unlock

PCL2 的「MC 下载功能已关闭」只是把入口短路了，功能本身还在。这个工具用 dnlib 改写 IL，恢复三处入口：**本体下载**（含 Forge / NeoForge / Fabric / LiteLoader / OptiFine）和**整合包安装**。

仅针对 **PCL2 2.13.1（2.99.0.0(413)）** 验证，其它版本先跑 `--check` 看是否匹配。非官方项目。

## 用法

### 1. 直接用现成的

`dist/PCL2.2.13.1.unlocked.exe` 是打好补丁的成品（SHA-256 `f7824e5f67b0653d04df2b244e7780bfe8879c152de62d05c2fb1d418588b45c`），改名成 `Plain Craft Launcher 2.exe` 覆盖原文件即可。

### 2. 跑脚本

```powershell
.\scripts\unlock.ps1 "D:\PCL2\Plain Craft Launcher 2.exe"
```

或双击 `scripts\unlock.bat`。脚本会构建、打补丁到 `dist\`、复核并打印 SHA-256。

```powershell
.\scripts\unlock.ps1 "D:\PCL2\PCL2.exe" -Only 1    # 只补本体下载
.\scripts\unlock.ps1 "D:\PCL2\PCL2.exe" -Check     # 只检查
```

### 3. 手动

```powershell
dotnet build src\PCL2Unlock
dotnet run --project src\PCL2Unlock -- "D:\PCL2\Plain Craft Launcher 2.exe" --check
dotnet run --project src\PCL2Unlock -- "D:\PCL2\Plain Craft Launcher 2.exe"
```

工具参数：`-o/--output` 输出路径，`--only 1,2,3` 选补丁，`-c/--check` 只检查不写文件。
退出码：`0` 成功，`2` 未解锁，`3` 版本不匹配（不写文件）。

## 需要的工具

| 工具 | 说明 |
|---|---|
| .NET SDK 8+ | 构建补丁工具，[下载](https://dotnet.microsoft.com/download) |
| dnlib 4.4.0 | 首次构建自动从 NuGet 还原 |
| Windows PowerShell 5.1 | Windows 10 / 11 自带 |

## 改了什么

| # | 位置 | 原来 |
|---|---|---|
| 1 | `PageDownloadInstall.LoaderInit()` | 真加载器被换成抛异常的「关闭下载」假加载器 → 下载页报错 |
| 2 | `ModModpack.ModpackInstall(File, …)` | 识别完包类型后无条件 `throw`，分派到各安装器的逻辑被删 |
| 3 | `ModModpack.ModpackInstall()` | 只剩一句「暂时不再提供 MC 下载功能」的提示 |

补丁只恢复接线，不改下载源、不改版本校验、不改变任何下载/安装逻辑；输出为新文件，原 exe 不动，重复执行会自动跳过已解锁的部分。

## 免责声明

本项目仅供**学习与研究**，用于演示 .NET 程序集结构分析与 IL 改写的方法，不作为对原软件的替代、破解分发或商业用途。

- 非官方项目，与 Plain Craft Launcher 2 的作者及 Mojang / Microsoft 均无关联；
- 不提供游戏本体、账号或任何商业内容；通过本工具下载的资源版权归各自权利人所有，请支持正版；
- `dist/` 与 Release 中的文件由原版程序改写而来，仅供本地测试，请在下载后 24 小时内删除；
- 使用本项目产生的一切后果（数据损坏、账号异常、违反相关协议等）由使用者自行承担，作者不提供任何担保。

## 许可

MIT，见 [LICENSE](LICENSE)。
