# LOL Chat OCR Translator (DEV)

[English](README.en.md) | 简体中文

> 面向《英雄联盟》对局聊天的安全截图 OCR 翻译辅助工具。  
> 通过框选聊天区域进行屏幕截图识别，将聊天内容清洗、去重、按 LOL 语境翻译，并显示在独立悬浮窗中。

![Windows](https://img.shields.io/badge/Windows-10%2F11-blue)
![.NET](https://img.shields.io/badge/.NET-8.0-purple)
![WPF](https://img.shields.io/badge/UI-WPF-0ea5e9)
![OCR](https://img.shields.io/badge/OCR-Windows%20OCR%20%7C%20PP--OCRv5-22c55e)
![Status](https://img.shields.io/badge/status-in%20development-f59e0b)

## 简介

LOL Chat OCR Translator 是一个 Windows 桌面工具，用来辅助理解和回复《英雄联盟》游戏内聊天。它不依赖游戏聊天接口，也不会读取游戏内存，而是只对用户框选的聊天区域进行截图 OCR，然后把识别出的聊天内容翻译到悬浮窗。

它适合这些场景：

- 在台服、东南亚服、日韩服等多语言环境中快速看懂队友聊天。
- 想把队友的短句、术语、黑话转换成更自然的本地语言表达。
- 想回复外语消息，但不希望工具自动发送内容。
- 想要一个不注入、不 Hook、不读内存的安全辅助翻译流程。

> ⚠️ 本项目不是 Riot Games 官方工具，与 Riot Games 或 League of Legends 没有关联。它只做屏幕截图 OCR、翻译和悬浮窗显示，不提供游戏自动化、自动发言、脚本操作或对局优势功能。

## 核心功能

### 1. 聊天区域截图 OCR

- 支持手动框选游戏聊天区域。
- 支持查看当前框选范围，方便确认 OCR 识别区域是否正确。
- 支持自动识别和手动识别一次。
- 支持 Windows OCR 和 PP-OCRv5 多语言版两种 OCR 引擎。
- 支持自动、中文+英文、英文、日文，以及高级多语言识别选项。
- 支持稳定、均衡、快速等自动识别速度设置。
- PP-OCRv5 环境可在程序内检测和安装，默认创建项目专用虚拟环境，不写入用户全局 Python。

### 2. 游戏聊天翻译

- 支持自动检测源语言。
- 支持输出到简体中文、繁体中文、英文、韩文、日文、越南文。
- 支持 MyMemory 免费翻译，无需 API Key。
- 支持 AI API / OpenAI-compatible 接口，可配置 API Base、API Key 和模型名。
- 支持 Ollama 本地模型，适合不想把聊天内容发送到云端 API 的用户。
- 内置翻译缓存，减少重复请求。
- 对疑似未翻译、半翻译、服务错误的结果会进行校验和跳过，避免把异常文本刷到悬浮窗。

### 3. 独立悬浮窗显示

- 识别结果显示在独立悬浮窗中，不修改游戏客户端。
- 支持置顶、透明度、字体大小、最大显示行数设置。
- 支持显示或隐藏发言人。
- 支持队伍、所有人、小队、系统等频道的颜色自定义。
- 支持鼠标穿透。
- 支持截图时隐藏或排除悬浮窗，避免 OCR 识别到自己的翻译结果。

### 4. 回复翻译到剪贴板

- 主窗口内置回复翻译输入框。
- 支持将输入内容翻译后复制到剪贴板，再由用户手动回游戏粘贴发送。
- 悬浮窗可开启输入框，直接在悬浮窗中输入要翻译的回复。
- 悬浮窗输入可跟随最近一条 OCR 聊天的源语言自动反向翻译，也可以手动固定目标语言。

### 5. 过滤与排除

- 可选择翻译输入时去掉用户名。
- 可选择隐藏频道标签。
- 可过滤系统消息、ping 消息、击杀提示、购买提示等非聊天内容。
- 支持排除指定玩家，最多 50 个 Riot ID。
- 排除玩家后，该玩家消息会在进入去重、词库匹配、翻译前直接跳过。

### 6. 快捷键

默认快捷键：

| 功能 | 默认快捷键 |
| --- | --- |
| 手动翻译一次 | `F8` |
| 开启 / 关闭自动翻译 | `F9` |
| 翻译剪贴板内容 | `Ctrl + Shift + T` |
| 打开设置窗口 | `Ctrl + Shift + S` |
| 重新框选聊天区域 | `Ctrl + Shift + R` |
| 查看当前框选范围 | `Ctrl + Shift + P` |
| 显示 / 隐藏悬浮窗 | `Ctrl + Shift + H` |
| 聚焦悬浮窗输入框 | `Ctrl + Shift + I` |

快捷键可在设置中重新配置或清空。

### 7. 安装、更新与环境管理

- 提供 WinForms 安装器，可自定义安装目录并创建桌面快捷方式。
- 安装器会注册 Windows 卸载项，方便从系统设置中卸载。
- 关于页面支持检查 GitHub Releases 最新版本。
- 可在程序内删除项目专用 PP-OCRv5 OCR 环境，不影响用户自己的 Python。

## 特色功能

### LOL 语境优化

本项目不是简单把 OCR 文本丢给翻译 API。它内置了面向 LOL 对局聊天的清洗、修复和术语处理流程：

- 内置 LOL 黑话、术语、短句和辱骂标签词库。
- 支持 `pls gank mid`、`plsgankmid`、`gank mid pls` 等常见抓人请求的本地直译。
- 支持 `ff`、`gg`、`ult`、`tp`、`ward`、`drake`、`baron` 等常见游戏术语。
- 针对 OCR 常见粘连文本进行修复，例如把 `ilikebanana`、`okilikechinesetoo` 这类识别结果切回更合理的英文短句。
- 对 LOL 频道名 OCR 错字进行容错匹配，例如队伍、所有人、小队等频道。

### 读取顺序与多行聊天处理

OCR 经常会把多行文本顺序识别错，或者把同一句聊天拆成几段。本项目在 OCR 后会继续做：

- 按 OCR 坐标修正从上到下、从左到右的阅读顺序。
- 对换行聊天进行合并，尽量还原完整句子。
- 对系统提示、训练模式提示、时间戳片段等内容做阻断，避免错误合并。
- 对长消息做稳定等待，减少“先输出半句，再输出完整句”的情况。

### 去重和稳定输出

游戏聊天区域会反复出现在截图里，如果不处理就会重复翻译。本项目加入了多层去重：

- 基于时间戳、发言人、消息正文的去重。
- 支持无时间戳聊天的去重。
- 翻译成功后才提交去重，避免翻译失败后同一句消息无法重试。
- 对同一帧内的多个候选消息按来源顺序输出，减少乱序。

### Live Client Data 辅助匹配

程序会尝试读取 LOL 本地 Live Client Data API 获取当前对局玩家信息，用于：

- 更准确识别发言人。
- 将英雄英文名、中文名、称号、别名关联起来。
- 辅助玩家排除、频道解析和悬浮窗显示。

如果 Live Client Data 不可用，程序仍会继续使用 OCR 文本进行翻译。

### 毒性内容显示策略

对辱骂、攻击性表达等内容，程序会优先在本地词库处理，而不是直接发送到普通翻译 API。用户可以选择不同显示方式：

- 安全标签：例如显示为 `[严重辱骂：家人攻击]`。
- 显示原意：可能展开为实际辱骂含义。
- 显示原始 OCR 文本：只显示游戏里识别到的原字符。
- 仅显示辱骂提示：隐藏具体内容。

### OCR 测试与诊断

内置 OCR 测试窗口，方便排查识别慢、识别错、框选不准等问题：

- 显示当前截图输入。
- 显示 OCR 实际输入图片。
- 显示识别耗时、模型信息、置信度、BoundingBox 等信息。
- 支持运行全部预处理对比，例如原图、灰度、对比度增强、二值化。
- 支持保存详细日志和调试图片。

## 截图

> 建议在发布前添加以下截图：主窗口、悬浮窗、设置窗口、OCR 测试窗口。  
> 可以放在 `docs/images/` 下，并在这里引用。

## 安装与使用

### 推荐方式：下载安装包

1. 前往 GitHub Releases 下载最新版安装包。
2. 运行 `LoLChatTranslator_Setup_x.x.x.exe`。
3. 选择安装目录，可选择是否创建桌面快捷方式。
4. 打开程序，点击“重新框选聊天区域”。
5. 如果使用 PP-OCRv5，在设置中点击“检测/安装 PP-OCRv5 OCR 环境”。
6. 选择翻译服务和目标语言。
7. 点击“开始自动翻译”。

### OCR 引擎选择建议

| 引擎 | 特点 | 适合场景 |
| --- | --- | --- |
| Windows OCR | 系统自带，配置简单 | 快速测试、轻量使用 |
| PP-OCRv5 多语言版 | 准确率更高，支持更多语言 | 长期使用、多语言对局、复杂聊天背景 |

## 从源码运行

### 环境要求

- Windows 10 / Windows 11 x64。
- .NET 8 SDK。
- 可选：Python 3.10 - 3.12 x64，用于 PP-OCRv5。
- 可选：Ollama，用于本地 AI 翻译。

> 由于项目使用 WPF 和 Windows OCR 相关 API，建议在 Windows 上构建和运行。

### 构建

```powershell
git clone https://github.com/NTide7/LoLChatTranslator.git
cd LoLChatTranslator

dotnet restore
dotnet build .\LoLChatTranslator.sln -c Release
```

### 运行

```powershell
dotnet run --project .\LoLChatTranslator\LoLChatTranslator.csproj
```

### 测试

```powershell
dotnet test .\LoLChatTranslator.Tests\LoLChatTranslator.Tests.csproj
```

### 打包安装器

```powershell
powershell -ExecutionPolicy Bypass -File .\Installer\build-installer.ps1
```

脚本会生成：

- `Installer\dist\LoLChatTranslator_Setup_1.0.0.exe`
- `Installer\dist\LoLChatTranslator_FrameworkDependent_1.0.0_win-x64.zip`

## 项目结构

```text
LoLChatTranslator/
├─ LoLChatTranslator/                 # WPF 主程序
│  ├─ MainWindow.xaml(.cs)             # 主窗口、自动 OCR、翻译流程
│  ├─ OverlayWindow.xaml(.cs)          # 悬浮窗
│  ├─ SettingsWindow.xaml(.cs)         # 设置窗口
│  ├─ OcrTestWindow.xaml(.cs)          # OCR 测试窗口
│  ├─ OCR/ppocrv5_multilingual.py      # PP-OCRv5 Python Worker
│  ├─ Resources/                       # 术语、黑话、频道别名资源
│  └─ Services/                        # OCR、翻译、清洗、去重、日志等服务
├─ LoLChatTranslator.Tests/            # xUnit 回归测试
├─ Installer/                          # WinForms 安装器与打包脚本
├─ Tools/                              # 辅助脚本
└─ LoLChatTranslator.sln
```

## 隐私与安全说明

- 程序只截取用户框选的屏幕区域。
- 程序不注入游戏进程。
- 程序不读取游戏内存。
- 程序不自动发送聊天消息。
- 回复翻译只复制到剪贴板，需要用户手动粘贴发送。
- 使用 MyMemory 或 AI API 时，被翻译文本会发送到对应服务商。
- 使用 Ollama 本地模型时，翻译请求可留在本机。
- 毒性内容、部分短句和术语会优先由本地词库处理。

## 当前状态

项目仍处于开发阶段，可能存在 OCR 速度、识别准确率、特殊分辨率适配、多语言术语覆盖等问题。欢迎通过 Issue 提交复现截图、日志和改进建议。

## 鸣谢

- Windows OCR / Windows.Media.Ocr
- PaddleOCR / PP-OCRv5
- MyMemory Translation
- Ollama
- Riot Data Dragon

## License

当前源码包中尚未包含 LICENSE 文件。公开发布前建议补充明确许可证。
