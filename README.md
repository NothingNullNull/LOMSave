# 活侠传自由存档 MOD · 2.0

通过游戏自带的存档/读档界面，在剧情和歇息场景保存当前进度，读档时从剧情边界快速回放到保存位置。也可用 F5/F9 快捷操作，读档后可检查属性和状态是否一致。

交流 QQ 群：398012894。

## 下载与安装

下载 [2.0.0 安装包](https://github.com/NothingNullNull/LOMSave/releases/download/v2.0.0/LOMSave-v2.0.0.zip)，或 [独立 LOMSave2.dll](https://github.com/NothingNullNull/LOMSave/releases/download/v2.0.0/LOMSave2.dll)。校验值见 [SHA256SUMS.txt](https://github.com/NothingNullNull/LOMSave/releases/download/v2.0.0/SHA256SUMS.txt)，发布说明见 [GitHub Release](https://github.com/NothingNullNull/LOMSave/releases/tag/v2.0.0)。

1. 关闭游戏并备份完整存档目录。
2. 安装 **BepInEx 5.x x86（32 位、Mono）** 到游戏根目录；本次验证环境为 5.4.23.5。[BepInEx 官方下载](https://github.com/BepInEx/BepInEx/releases)
3. 若装过旧版 MOD，移走 `BepInEx/plugins` 下的旧 `LOMSave.dll`；避免同时加载两个实现。旧整合包使用 BepInEx 6，应先按官方方式切换加载器，不能混用 5/6 的核心文件。
4. 将安装包解压到游戏根目录，最终 DLL 路径为 `BepInEx/plugins/LOMSave2/LOMSave2.dll`。独立 DLL 也放到此处。
5. 重启游戏，直接通过游戏自带的存档/读档界面使用；需要快捷操作时，也可使用下方快捷键。

安装包只包含本 MOD 和说明，不包含 BepInEx、游戏程序集、游戏资源或玩家存档。

## 使用方式

**不使用快捷键也能正常存读档。** 打开游戏自带的存档界面，选择槽位保存；需要读取时，在读档界面选择对应存档即可。剧情和歇息场景的普通保存同样记录精确回放资料与验证基准，普通读档同样自动回放并验证。

快捷键只是便捷入口，与游戏的存读档共用同一套机制。F5/F9 操作系统当前槽位；首次还没有当前槽位时，先通过游戏存档菜单选择一个槽位保存。

| 快捷键 | 操作 |
| --- | --- |
| F5 | 覆盖系统当前槽位 |
| F9 | 读取系统当前槽位，标题界面也可使用 |
| F8 | 比较读档后的状态与保存时基准，只读取、不修改数值 |

例如当前槽位是 003，F5 保存的就是 003，F9、“最近存档”和编号 003 都能找到它；切换系统槽位后快捷键也跟随切换。自由行动场景使用原生存读档；剧情和歇息场景额外记录精确回放资料。

剧情中建议在对话或选项暂停处保存。回放期间等到完成提示再操作；场景切换、掷骰和交互面板处理中暂时不能保存。

读档后默认自动验证。默认报告位置：`BepInEx/plugins/LOMSave2/verification/latest.txt`，每次报告另有存档槽与时间对应的历史副本。

- `PASS`：参与比较的值一致，且未检测到回放异常。
- `DIFF`：发现差异，查看报告中的字段、保存值和读取值。
- `INCOMPLETE`：回放或验证未完成，不能视为一致。

详细步骤见 [存读档状态验证](docs/存读档状态验证.md)。

## 配置

首次启动后生成 `BepInEx/config/com.nn.lomsave2.cfg`。

| 配置 | 默认值 / 用途 |
| --- | --- |
| `Hotkeys.QuickSave` / `QuickLoad` / `Verify` | F5 / F9 / F8 |
| `Replay.TimeScale` | 12，回放速度 |
| `Replay.ShowOverlay` | true，回放遮罩 |
| `Verification.AutoCheck` | true，回放完成后自动检查 |
| `Verification.ReportDirectory` | 验证报告目录 |

## 存档兼容与限制

- 原生 `.dat` 仍由游戏保存；剧情附加档为同名 `.dat.loms`，需要一起备份、移动。
- 当前附加档格式为 4，接受格式 3。旧格式 3 若缺少命运面板记录，需要按原选择人工确认，完成回放后重新保存，才会补入记录。
- 格式 2、损坏或配对不一致的附加档回退到原生读取；恢复点可能早于保存时的对话位置。
- **旧仓库 1.x 的 `.ss` / `.ss.json` 不会自动转换为 `.loms`。** 保留完整旧档备份，先核对原生读档后的进度，再用 2.0 重新保存；不能保证旧 MOD 的精确对话停点。
- 开发过程中的 2.1.x～2.3.x 已整合为此次正式发布的 **2.0.0**；插件 GUID、程序集身份与附加档格式保持兼容。
- 实机回放仍有 `FreeRandomState.s0..s3` 四项已知差异，报告保留 `DIFF`；后续随机结果可能不同，不能宣称全部状态一致。
- 当前不完整拦截跨游戏版本或脚本变化。游戏更新后先按 [兼容性检查清单](游戏更新兼容性检查清单.md) 用存档副本验证。
- UI 动画、语音播放位置，以及 Lua 局部变量、表、函数不在状态快照比较范围内。其他修改相同接口的 MOD 需要单独验证兼容性。

## 从源码编译

需要 .NET SDK、本地游戏与 BepInEx 5 x86。依赖程序集从游戏安装目录读取，不随仓库分发。以下命令在仓库根目录执行，将占位符替换为实际游戏安装目录。

```powershell
$gameDir = '<游戏安装目录的完整路径>'
dotnet build .\LOMSave2\LOMSave2.csproj -c Release -p:Deploy=false "-p:GameDir=$gameDir"
```

输出：`LOMSave2/bin/Release/LOMSave2.dll`。省略 `-p:Deploy=false` 会将 DLL 复制到指定游戏目录；仅检查编译时保留该参数。

运行自动检查与生成发布包：

```powershell
python -m pip install -r .\tools\requirements.txt
$gameDir = '<游戏安装目录的完整路径>'
.\tests\Run-Tests.ps1 -GameDir $gameDir
.\tools\Build-Release.ps1 -GameDir $gameDir
```

测试缺少剧情资料时会从本地游戏提取两个用例，提取文件保留在被忽略的 `assets/story/`。发布包默认输出到 `dist/`，构建不部署到游戏。测试范围和限制见 [tests/README.md](tests/README.md)。

源码位于 `LOMSave2/`，测试位于 `tests/`，构建工具位于 `tools/`。版本说明见 [CHANGELOG.md](CHANGELOG.md)。
