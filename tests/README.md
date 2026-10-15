# LOMSave 2.0 自动检查

在仓库根目录执行，将占位符替换为实际游戏安装目录：

```powershell
python -m pip install -r .\tools\requirements.txt
$gameDir = '<游戏安装目录的完整路径>'
.\tests\Run-Tests.ps1 -GameDir $gameDir
```

`-Python` 可指定 Python 可执行文件。需要 .NET SDK、本地游戏程序集和与游戏位数一致的 BepInEx 5 Mono（新版为 x64）；测试构建始终禁用自动部署，不读取或修改玩家存档。

缺少剧情资料时，入口脚本从本地游戏提取 `back_02_501`、`ch2_4_5_3` 到 `assets/story/`，不分发游戏脚本。游戏更新后可手动重新提取：

```powershell
$gameDir = '<游戏安装目录的完整路径>'
python .\tools\Extract-TestStories.py --game-dir $gameDir
```

当前基线为 48 项通过、31 个补丁方法目标及参数检查，使用游戏 `release_1.0.6000.2`。测试入口会输出本次测试资料与 `results.txt` 的位置。

## 覆盖范围

- 原生存档与附加档配对、原子替换、覆盖清理和写入失败处理。
- 旧格式反序列化、格式 2 回退、格式 3 无验证快照读取、格式 4 命运事件。
- Lua 跨脚本标量变量、脚本内自动存档边界、读取入口隔离和等待位置。
- 原生读取恢复存档起点，不沿用上一会话值，也不改写非回放路径的运行剧情；空起点值按原值恢复。
- 状态快照独立性、差异定位、集合顺序与时间戳、报告和异常回放判定。
- 排除主档/嵌入还原存档的 `FreeRandomState`，保留同名 Lua 变量与事件标记差异；0/1/2 项有效差异的报告与玩家警告阈值。
- 命运属性/好感/标记、扣点与撤销、多个面板和起始状态校验。
- Harmony 目标方法和注入参数与本地游戏程序集匹配。
- 游戏、Unity、Mono 与 Doorstop 加载器位数匹配；人物介绍仅展示模式不激活关系。

历史格式测试使用 `fixtures/format2/LOMSave2.dll` 和 `fixtures/format3/LOMSave2.dll`，在独立进程中生成合成测试档；这些 DLL 是历史 MOD 构建产物，不是游戏程序集，也不应安装到游戏。

## 发布包检查

执行 `tools/Build-Release.ps1` 时自动检查两种包内 DLL、整合包根目录结构、官方 BepInEx 全部文件、第三方许可与源码及发布校验文件；失败会阻止发布。

```powershell
python .\tools\package_bundle.py verify --release-dir .\dist --version 2.0.2
python .\tests\test_release_package.py --release-dir .\dist --version 2.0.2
```

将版本号替换为本次发布版本。发布包测试在项目 `.work/` 内模拟解压覆盖，确认已有配置、其他插件和存档标记保持原样；同时检查缺包、缺少校验项、错误组件、重复 MOD、个人文件和多余外层目录会被拒绝。此检查不代替实际游戏启动验证。

## 限制

独立 CLR 无法执行 Unity 内部 ECall，测试中的引擎交互使用替身，不能证明实际 Harmony 安装、场景生命周期、输入或界面正常。MoonSharp 初始化时可能输出 UnityAssetsScriptLoader 的 SecurityException；最终以各项结果和退出码判断，不能把该信息当成游戏实机日志。

`ch2_4_5_3` 边界用例截取至 `D_2_4_5_3_001` 对话，检查两句对话、自动存档、选择和分支对话的顺序。更新脚本后若结构改变，应重新核对有效边界，不能直接放宽断言或把用例失效当成游戏兼容性结论。

实机检查见 [游戏更新兼容性检查清单](../游戏更新兼容性检查清单.md)。`FreeRandomState` 不计入差异，原值仍保留在完整报告并标记为已忽略。
