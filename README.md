# pcl2-unlock

PCL2 的「MC 下载功能已关闭」**只是被短路，并没有删除**。这个工具用 [dnlib](https://github.com/0xd4d/dnlib) 直接改写 IL，把三处拦截点补回去：

- 「下载 → 游戏本体」的版本列表重新可用，进而能正常安装原版 / Forge / NeoForge / Fabric / LiteLoader / OptiFine；
- 「安装整合包」按钮重新弹出文件选择框，CurseForge / Modrinth / HMCL / MMC / MCBBS / 启动器包 / 纯压缩包都能装。

> 在 **PCL2 2.13.1（内部版本 2.99.0.0(413)）** 上定位并验证。其它版本未验证，先跑 `--check` 看是否匹配。
> 非官方项目，与 PCL 开发者无关。

## 被改成了什么样

官方在某次更新里把下载入口关掉，提示语是：

```
为遵守相关法律法规，PCL 暂时不再提供 MC 下载功能，非常抱歉 qwq……
```

反编译后可以看到，下载/安装的实现一行没少，只是三个入口被短路：

| # | 位置 | 现状 | 后果 |
|---|---|---|---|
| 1 | `PageDownloadInstall.LoaderInit()` | 真加载器 `ModDownload.DlClientListLoader` 被换成名叫「关闭下载」的假 `LoaderTask`，其 lambda 直接 `throw` | 进下载页只看到错误页 |
| 2 | `ModModpack.ModpackInstall(File, InstanceName, Logo)` | 完整解析 zip、识别出包类型（局部变量 `num`）之后，末尾无条件 `throw`，原来的「按 `num` 分派」被整段删掉 | 拖放 / 双击整合包弹红字 |
| 3 | `ModModpack.ModpackInstall()` | 方法体只剩一句 `ModMain.Hint("为遵守相关法律法规……")` | 「安装整合包」按钮只弹红字 |

底下这些全部完好，所以补丁只是「重新接线」：

`ModDownload.DlClientListLoader`（仍请求 `launchermeta.mojang.com` / BMCLAPI 镜像）、`ModDownloadLib.McDownloadClientLoader`、`McInstallLoadersGet`、`McInstallStart`、`McInstallCommit`、`McDownloadForgelikeLoader`、`ShowMinecraftVersionList`、`BtnStart_Click`、7 个 `InstallPack*`。

## 用法

需要 .NET SDK 8 及以上（项目目标是 `net10.0`，改 `TargetFramework` 即可降级）。dnlib 纯托管，macOS / Linux / Windows 都能跑。

```bash
# 1. 构建
dotnet build src/PCL2Unlock

# 2. 先看这个 exe 是否匹配（只读，已解锁返回 0，未解锁返回 2）
dotnet run --project src/PCL2Unlock -- "Plain Craft Launcher 2.exe" --check

# 3. 打补丁：默认输出 <输入名>.unlocked.exe
dotnet run --project src/PCL2Unlock -- "Plain Craft Launcher 2.exe"

# 只补其中一处；或自定义输出
dotnet run --project src/PCL2Unlock -- PCL2.exe -o PCL2.unlocked.exe --only 1
```

| 参数 | 说明 |
|---|---|
| `-o, --output <路径>` | 输出文件，默认 `<输入名>.unlocked.exe` |
| `--only 1,2,3` | 只跑指定补丁 |
| `-c, --check` | 只检查是否已解锁，不写文件（0 = 已解锁，2 = 未解锁，3 = 版本不匹配） |
| `-h, --help` | 帮助 |

输出是**新文件**，原 exe 不动。补丁是幂等的：对已解锁的文件再跑一次会全部跳过，字节不变。

## 补丁做了什么

### 1. 接回真实的版本列表加载器

```diff
- PageLoaderInit(LoadMinecraft, PanLoad, PanAllBack, null,
-     new LoaderTask<string, DlClientListResult>("关闭下载", task => { throw new Exception("为遵守相关法律法规……"); }),
-     task => LoadMinecraft_OnFinish());
+ PageLoaderInit(LoadMinecraft, PanLoad, PanAllBack, null,
+     ModDownload.DlClientListLoader,
+     task => LoadMinecraft_OnFinish());
```

IL 层面：把「假加载器」的构造区间（13 条指令）整体换成一条 `ldsfld ModDownload::DlClientListLoader`。
`LoadMinecraft_OnFinish()` 本来就从 `DlClientListLoader.Output["versions"]` 取列表，接回去即可。

### 2. 补回整合包类型分派

原方法识别完包类型就 `throw`，分派表被删。这里新增一个静态方法，并把末尾的 `throw` 换成「分派 + 返回」：

```csharp
public static LoaderCombo<string> ModpackInstallDispatch(
    string File, ZipArchive Archive, string BaseFolder, int Kind, string InstanceName, string Logo)
    => Kind switch
    {
        0 => InstallPackCurseForge(File, Archive, BaseFolder, InstanceName, Logo),
        1 => InstallPackHMCL(File, Archive, BaseFolder),
        2 => InstallPackMMC(File, Archive, BaseFolder),
        3 => InstallPackMCBBS(File, Archive, BaseFolder, InstanceName),
        4 => InstallPackModrinth(File, Archive, BaseFolder, InstanceName, Logo),
        9 => InstallPackLauncherPack(File, Archive, BaseFolder),
        _ => InstallPackCompress(File, Archive),
    };
```

`Kind` 就是原方法里那套残留的识别逻辑写下的 `num`（清单文件 → 编号），补丁没有改动它，只补了后续分派：

| num | 判据 | 安装器 |
|---|---|---|
| 0 | `manifest.json` 且无 `addons` | CurseForge |
| 1 | `modpack.json` | HMCL |
| 2 | `mmc-pack.json` | MultiMC / Prism |
| 3 | `mcbbs.packmeta`，或 `manifest.json` 带 `addons` | MCBBS |
| 4 | `modrinth.index.json` | Modrinth |
| 9 | 嵌套的 `modpack.zip` / `modpack.mrpack` | 启动器包 |
| 其它 | — | 纯压缩包 |

实现细节：原方法的 `throw` 三条指令保留为不可达填充（它同时是 `try/finally` 的边界），分派代码插在它前面并用 `leave` 跳出；追加的 `ret` 之前要把 `finally` 的 `HandlerEnd` 钉住，否则 `leave` 会落进异常处理区间，CLR 直接判无效 IL。

### 3. 还给按钮一个文件选择框

无参入口原本应该是「弹框 → 调三参重载」。补丁内联重建了整个方法体：

```csharp
public static void ModpackInstall()
{
    string text = Dialogs.SelectFile("选择整合包文件", false, null,
        (OneOf<IEnumerable<(string[], string)>, string>?)("整合包", new[] { "zip", "mrpack", "rar" }))
        .FirstOrDefault();
    if (!string.IsNullOrEmpty(text)) ModpackInstall(text);
}
```

泛型实例化（`ValueTuple<string[], string>`、`OneOf<…>`、`Nullable<OneOf<…>>`、`Enumerable.FirstOrDefault<string>`）不手搓签名，直接从程序集里已有的同类调用点回收成员引用，避免写错元数据。

## 验证

```bash
ilspycmd -t PCL.PageDownloadInstall PCL2.unlocked.exe   # LoaderInit 里应是 ModDownload.DlClientListLoader
ilspycmd -t PCL.ModModpack PCL2.unlocked.exe            # 应有 ModpackInstallDispatch；无参重载应是 SelectFile + ModpackInstall(text)
ilspycmd --list-resources PCL2.unlocked.exe             # 资源表与原版逐条一致
```

运行侧：

- 「下载 → 游戏本体」应出现 正式版 / 预览版 / 远古版 / 愚人节版 分组列表，日志出现 `加载器 LoaderTask\`2 DlClientList Main 状态改变：Finished`；
- 选版本 → 开始安装，任务栏出现 `<名字> 安装`、`下载原版 <版本>`；
- 「安装整合包」按钮弹框，选中后任务栏出现 `CurseForge/Modrinth/HMCL/MMC/MCBB 整合包安装：<名字>`。

## 已知限制

**必须放在盘符路径下运行。** PCL 的 `ModNet.LoaderDownload` 里有一处硬性检查：

```csharp
file.LocalPath = file.LocalPath.Replace("/", "\\");
if (!file.LocalPath.Contains(":\\"))
    throw new ArgumentException("输入的本地文件地址不正确：" + file.LocalPath);
```

所以路径形如 `\\Mac\Home\...`（Parallels / VMware 的 Mac 共享文件夹，或 Wine 的 UNC 路径）时，下载初始化必然失败——**这不是补丁的问题**，本体下载、资源下载都过不去。解法：

1. 把 PCL 和 `.minecraft` 都放到虚拟机本地盘（`C:\PCL2\`），从本地盘启动；
2. 或把共享文件夹映射成盘符：`net use X: \\Mac\Home`，再用 `X:\...` 启动；
3. 或在 macOS 上直接用 Prism Launcher / 官方启动器等原生方案。

其它：

- `.rar` 整合包仍会按 PCL 原逻辑提示「请在解压后重新压缩为 zip」；
- 补丁只恢复接线，不改下载源、不绕过版本校验，网络问题（例如 `maven.neoforged.net` 超时）照旧。

## 许可

MIT，见 [LICENSE](LICENSE)。
