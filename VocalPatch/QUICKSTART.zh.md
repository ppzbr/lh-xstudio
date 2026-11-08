# VocalPatch 快速上手

对标 VOCALOIDPatcher 的功能，覆盖 **CeVIO / Synthesizer V Studio / VoiSona / OpenUtau / Piapro Studio**。
只做功能，不做汉化。

它干的事：

- 五种编辑器的工程格式互转（`.ccs` `.svp` `.tmpproj` `.ustx` `.ust` `.vsqx` `.vpr` `.ppsf` `.mid` `.musicxml` `.lrc` …）
- 音符处理：摇摆（swing）、量化、断音（staccato）、移调、力度归一、和声加轨、歌词替换、中文转拼音
- 自动备份：运行中定时快照 + 每次保存时快照
- 全部动作都有 `<文件>.bak` 兜底，随时可还原

---

## 目录里有什么

```
dist\
  QUICKSTART.zh.md                     本文件
  loader\vocalpatch_loader_x64.dll     可注入的原生加载器（64 位）
  loader\vocalpatch_loader_x86.dll     可注入的原生加载器（32 位）
  injector\vpatch-inject.exe           注入器 + 动作命令行
  cli\vpatch.exe                       纯命令行：格式转换与作业
  payload\                             托管运行时（连加载器一起注入）
    VocalPatch.Editor.dll              被加载器启动的入口
    vocalpatch_loader.dll              64 位加载器，放在这里省事
    vpatch-editor.ini                  运行时配置
    .parked\                          重新打包时被占用的旧文件，关掉编辑器后可整个删掉
  openutau\VocalPatch *\               已经做好的 OpenUtau 插件文件夹
```

---

## 用法一：OpenUtau（不用注入，最省事）

OpenUtau 有自己的插件接口，所以直接用它。

1. 把 `dist\openutau\` 里的每个 `VocalPatch xxx` 文件夹整个复制到
   `<OpenUtau 安装目录>\Plugins\`
2. 重启 OpenUtau
3. 选中音符 → 插件菜单里就会出现 `VocalPatch Quantize`、`VocalPatch Swing`、
   `VocalPatch Staccato`、`VocalPatch Transpose +12`、`VocalPatch Harmony Third Up`、
   `VocalPatch Pinyin` 等条目

原理：OpenUtau 会把选中的音符写成临时 UST 交给插件，插件改完写回，
**所有没被动作碰到的行原样保留**（不会像常见插件那样把轨道信息吃掉）。

---

## 用法二：注入 CeVIO / SynthV / VoiSona / Piapro

这四个是 .NET Framework 宿主，用 `payload` 里的加载器把托管运行时送进去。

```
dist\injector\vpatch-inject.exe targets
```

先看一遍正在跑的编辑器（输出里能直接抄进程名或 pid）。然后：

```
dist\injector\vpatch-inject.exe inject synthv-studio "D:\...\dist\payload\vocalpatch_loader.dll"
dist\injector\vpatch-inject.exe inject CeVIO         "D:\...\dist\payload\vocalpatch_loader.dll"
dist\injector\vpatch-inject.exe inject VoiSona       "D:\...\dist\payload\vocalpatch_loader.dll"
dist\injector\vpatch-inject.exe inject Piapro        "D:\...\dist\payload\vocalpatch_loader.dll"
```

**位数必须一致**：64 位编辑器注入 `payload\vocalpatch_loader.dll`（=x64 那份）。
32 位的老编辑器要注入 `loader\vocalpatch_loader_x86.dll`，而且注入器也得是 32 位：

```
dotnet publish src\VocalPatch.Injector -c Release -r win-x86 --self-contained false -o dist-injector-x86
```

### 怎么算注入成功

注入是异步的，进程要活着几秒。两个日志文件是唯一判据：

- `%TEMP%\vocalpatch-loader.log` —— 原生加载器的日志
  正常结尾是 `loader ok`
- `%TEMP%\vocalpatch-editor.log` —— 托管运行时的日志
  正常内容类似：

```
2026-11-07 22:25:55  [26704]  profile: Synthesizer V Studio
2026-11-07 22:25:55  [26704]  watching 1 folder(s)
2026-11-07 22:25:55  [26704]    . D:\文档\Dreamtonics
2026-11-07 22:25:55  [26704]  patched 5 save method(s)
2026-11-07 22:25:56  [26704]  hotkey: Ctrl+Alt+V, Ctrl+Alt+S, Ctrl+Alt+Q, Ctrl+Alt+C, Ctrl+Alt+T, Ctrl+Alt+N, Ctrl+Alt+P, Ctrl+Alt+H, Ctrl+Alt+L, Ctrl+Alt+E, Ctrl+Alt+Z
2026-11-07 22:26:10  [26704]  hotkey snapshot -> D:\...\.vocalpatch-autosave\song.20261107-222610.svp
```

每行都带 pid：**多个编辑器共用一个日志文件**，按 pid 分组看才不会串。

失败时看得到具体哪一步断的，例如
`CLRCreateInstance` / `GetRuntime` / `GetInterface(ICLRRuntimeHost)` 后面跟着非 0 的 HRESULT。

**"每次保存自动快照"只对托管宿主有效**，日志会把判定结果写出来，两种情况各一行：

```
host code in this runtime: CeVIO AI.orig.exe - its saves pass through the hook
note: this host's own code is not in this AppDomain (a native host, or one on its own runtime) - the save hook cannot see its saves; the folder watcher and the hotkeys still work
```

原理：加载器自带一套 .NET Framework runtime。若宿主**本身就是 .NET Framework 程序**，
那我们用的就是宿主那一套 runtime，补丁打在同一份 BCL 上，宿主保存时会经过我们 → 第一种。
否则（宿主是原生程序，或跑在 .NET 5+ 自己的 runtime 上）补丁只能拦到我们自己 → 第二种。

实测本机五家的结果：

| 编辑器 | 宿主类型 | 每次保存快照 | 文件夹监视 | 热键 |
|---|---|---|---|---|
| CeVIO AI（`CeVIO AI.orig.exe`） | .NET Framework（WPF） | **生效** | 生效 | 生效 |
| Synthesizer V Studio 2 Pro | 原生（Qt），只为脚本动态挂 .NET | 无效（保存走原生代码） | 生效 | 生效 |
| VoiSona | 原生 | 无效 | 生效 | 生效 |
| Piapro Studio NT | 原生 | 无效 | 生效 | 生效 |
| OpenUtau | .NET 5+（CoreCLR） | 无效 | 生效 | 生效 |
| VOCALOID6 | .NET 5+（CoreCLR）+ WPF | 无效 | 生效 | 生效 |

结论：**自动备份主要靠文件夹监视**（文件一变就快照），保存钩子是 CeVIO 的额外加分项。
监视范围由 profile 的目录决定；工程放在这些目录之外时，用 `watch=<目录>` 补上。

### 多个编辑器同时注入

五个编辑器可以同时注入，互不冲突，但热键遵守一条规则：

**只有前台窗口属于哪个编辑器，热键就在哪个编辑器里生效。**

原因是每个被注入的进程都会装一个全局键盘钩子，谁先轮到谁就能把按键吞掉。
若不判断前台，按下 `Ctrl+Alt+V` 会作用在"链条里排第一"的那个编辑器上——多半不是你在看的那个。
现在非前台的编辑器会在日志里写一行 `ignored (not the foreground editor)` 并把按键放行。

### 注入后能按的热键

热键作用对象是「被监视目录里最近修改的那个工程文件」。全部是 `Ctrl+Alt+`

| 热键 | 动作 | 说明 |
| --- | --- | --- |
| `V` | snapshot | 立刻备份一份到 `.vocalpatch-autosave\` |
| `S` | swing | 8 分音符摇摆 55% |
| `Q` | quantize | 16 分量化，力度 100% |
| `C` | staccato | 断音：每个音缩短，后面补休止，总时长不变 |
| `T` | transpose | 整体升八度（12 个半音） |
| `N` | normalize | 力度归一 |
| `P` | pinyin | 歌词中文 → 拼音（原地改，先备份） |
| `H` | harmony | 加一轨上方三度和声 |
| `L` | lyrics | 剪贴板里的歌词铺到音符上（一行一个音节） |
| `E` | export | 导出成其它编辑器的格式，落到 `vocalpatch-export\` |
| `Z` | restore | 用 `.bak` 还原上一次原地修改 |

十一个动作全部有默认键位。`L` 之所以读剪贴板：热键没法让你打字。先把歌词一行
一个音节复制好（`你好` / `世界` / `唱歌`），按 `L` 后从第一个音符开始循环铺满；
剪贴板为空就什么都不做，不会清空已有歌词。

想改键位就编辑 `payload\vpatch-editor.ini`：

```ini
# 字母 / 0x51 / 81 三种写法都行
hotkey.swing=0x54
```

**按了没反应怎么查**：每按一次热键都会往 `%TEMP%\vocalpatch-editor.log` 记一行
结果，格式是 `<动作> -> <结果>`。如果日志里**完全没有**这一行，说明按键没送到钩子上，
把诊断开关打开：

```ini
hotkeydebug=true     # 会打印每个按键的 vk 码和 ctrl/alt 状态
```

正常应当看到 `key vk=0x56 down ctrl=True alt=True` 紧跟着 `hotkey fired vk=0x56`。
如果是 `hotkey vk=0x56 ignored (not the foreground editor)`，说明焦点不在这个编辑器上
（见上面「多个编辑器同时注入」）。

**不会被当成工程的文件（两类）**：

1. **我们自己产生的**：`.vocalpatch-autosave\`、`vocalpatch-export\`、`*.bak`。
   快照的扩展名和原工程一样，若不排除，自动备份就会把自己的快照再快照一次，层层嵌套。
2. **编辑器自己的临时文件**：`recovery\`、`Backups\` 目录，以及文件名里带 `autosave` /
   `auto-save` / `~$` 的。这类文件扩展名和真工程一样，实测里 Synthesizer V 会往
   `…\recovery\*.svp` 写崩溃恢复副本，OpenUtau 会往 `Backups\*-autosave.ustx` 写自己的自动保存，
   不排除的话每轮都会被复制一份（单个文件 360 KB 起，无限堆积）。

---

## 用法三：命令行直接处理工程文件

不用开编辑器也能跑同一套动作（注入运行时和这里是同一段代码）：

```
dist\cli\vpatch.exe formats                          # 列出支持的格式
dist\cli\vpatch.exe info "song.svp"                  # 看看解析出什么
dist\cli\vpatch.exe convert "song.svp" "song.ust"    # 换格式
dist\cli\vpatch.exe job swing --set division=8 --set amount=55 -o "out.ust" "song.svp"
dist\cli\vpatch.exe pinyin -o "out.ust" "song.ust"

dist\injector\vpatch-inject.exe actions              # 列出动作
dist\injector\vpatch-inject.exe action "song.ust" swing
dist\injector\vpatch-inject.exe action "song.ust" export
dist\injector\vpatch-inject.exe action "song.ust" restore
```

`action` 和热键走的是同一个入口，所以行为完全一致：原地改、留 `.bak`、可还原。

---

## 配置：`vpatch-editor.ini`

放在 `VocalPatch.Editor.dll` 旁边（也就是 `dist\payload\`）。全部可选：

```ini
profile=voisona        # 强制指定 profile，不写就按宿主进程名判断
autosave=true          # 运行中定时快照
patchsaves=true        # 每次保存都顺手快照
snapshotinterval=30    # 同一个文件两次快照之间的最小间隔（秒）
watch=D:\我的工程;E:\备份\vsqx   # 额外监视目录，分号分隔
hotkey.harmony=0x4A    # 改键位
hotkeydebug=false      # 打印钩子看到的每一个按键（排查热键用）
```

`profile` 三种写法都认：短 id（`synthv`）、进程名（`synthv-studio`）、显示名
（`Synthesizer V Studio`）。干脆写错也没关系：认不出来时会退化成「监视全部目标目录」。

进程名是**前缀匹配**，因为真实的可执行名常带后缀：`CeVIO AI.orig`（套壳启动器里的原名）、
`Piapro Studio NT`、`VoiSona1.18` 都能正确识别成对应 profile。

---

## 自己验证（不碰真编辑器的两个小工具）

`tools\` 里有两个探针，用来在不打开编辑器的情况下证明运行时真的活着：

- **`tools\hotkey_host.exe`**：一个最小的 .NET Framework 窗口程序（Windows Forms）。
  注入它，让窗口获得焦点，按 `Ctrl+Alt+V`，就能验证热键链路。
  它必须有真窗口：前台判定读的是 `GetForegroundWindow()`，控制台窗口属于 `conhost.exe`，判定会（正确地）拒绝。
- **`tools\savehook_probe.ps1`**：一个 .NET Framework 的 PowerShell 宿主，等 15 秒后自己用
  `File.WriteAllText` / `WriteAllBytes` / `XmlDocument.Save` / `XDocument.Save` 各写一个 `.ust`。
  注入它以后应当看到 4 行 `save-hook -> …` —— 这证明保存钩子拦到的是**宿主自己**的写入，
  不是只拦到我们自己的。`StreamWriter` 不触发，因为它不在补丁清单里。

`tools\hotkey_host.cs` 是前者的源码，用机器上现成的 .NET Framework 编译器构建：

```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe ^
  /out:tools\hotkey_host.exe tools\hotkey_host.cs ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll
```

---


- **`.ccs`（CeVIO）没有公开格式规范**：读是宽容解析，写是按惯例推测。能读写音符，但 CeVIO 自己的私有字段无法保证。
- **OpenUtau 通路固定在 480 ticks/quarter**，且一个音符不能拆成多个；`OpenUtau → 其它格式 → OpenUtau` 才是无损的。
- **深层的界面级修改需要逐版本逆向**（比如改钢琴卷帘的绘制）。当前做的是工程文件层面：格式互转、音符作业、自动备份、热键触发，这些是跨版本稳定的。
- **保存钩子只对 .NET Framework 宿主生效**（见上面那张表）：OpenUtau / VOCALOID6 跑在 .NET 5+ 自己的 runtime 上，SynthV / VoiSona / Piapro 的保存走的是原生代码 —— 这几家的"每次保存快照"不生效，由文件夹监视兜底。要让 CoreCLR 宿主也吃到保存钩子，得换成 `hostfxr`/`nethost` 加载器。
- **自动备份的作用范围**：文件夹监视只覆盖 profile 声明的目录，工程放在别处时用 `watch=<目录>` 补上；保存钩子不受目录限制，但只对托管宿主有效（本机就是 CeVIO）。
- 注入第三方进程本身有风险，**先保存好工程**再玩；所有原地修改都留 `.bak` 和 `.vocalpatch-autosave\`，出问题先按 `Ctrl+Alt+Z`。
- 运行中重新打包时，被编辑器映射的旧文件覆盖不了，只能改名挪进 `dist\payload\.parked\`。**关掉编辑器后可以把 `.parked\` 整个删掉**；打包脚本每次启动都会顺手清理一轮。
