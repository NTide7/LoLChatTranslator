# Coming Soon 预计5月23日发布

# LoL Chat Translator / 英雄联盟聊天翻译器

一个基于 OCR 的《英雄联盟》实时聊天翻译悬浮窗工具。

本工具会截取游戏聊天区域，通过 OCR 识别聊天内容，自动清理时间、频道、用户名和英雄名，并对常见游戏黑话、缩写和术语进行归一化处理，最后将翻译结果显示在可自定义颜色的悬浮窗中。

> 本项目不是 Riot Games 官方工具，与 Riot Games 无关。

---

## 功能特性

- OCR 识别《英雄联盟》聊天栏内容
- 支持队伍 / 所有人 / 小队频道识别
- 支持多语言频道标签识别
- 支持频道颜色自定义
- 支持 Windows OCR、PaddleOCR、RapidOCR
- 支持游戏黑话、缩写、术语预处理
- 支持用户名与英雄名清理
- 支持当前对局玩家名纠错
- 支持翻译结果悬浮窗显示
- 支持剪贴板翻译辅助
- 不自动发送聊天，所有发言仍由用户手动确认

---

## 安全说明

本项目不会使用Riot Games 官方禁止使用的手段

---

## 翻译与文本预处理

本工具会在翻译前处理常见 LOL 聊天缩写，例如：

| 原文 | 处理后 |
|---|---|
| `r u ok?` | 你还好吗？ |
| `u r ad?` | 你是 AD 吗？ |
| `pls gank mid` | 请来中路抓一下 |
| `jg no f` | 打野没闪 |
| `mid no flash` | 中路没闪 |
| `omw` | 我在路上 |
| `mb` | 我的锅 |

这样可以避免普通翻译器把 `mid` 翻译成“中间车道”，或者把 `AD` 翻译成“广告”。

同时该工具调用了《英雄联盟》的本地对局详情接口以获取对局内用户名（英雄），以便纠正OCR的结果。

---

## 使用方法

1. 启动程序。
2. 在设置中选择 OCR 引擎。
3. 设置聊天栏截图区域。
4. 选择翻译方式。
5. 开启自动识别。
6. 游戏内聊天内容会显示在悬浮窗中。

设置页包含一键安装依赖的按钮，所以建议优先使用：

```text
RapidOCR
```

如果不想安装本地 OCR 环境，可以先使用：

```text
Windows OCR
```

---

## 配置说明

程序会使用配置文件保存设置，例如：

```text
appsettings.json
```

你可以在设置界面中修改：

- OCR 引擎
- OCR 截图区域
- 图片放大倍率
- 对比度
- 翻译引擎
- 频道颜色
- 悬浮窗透明度
- 字体大小
- 快捷键

高级用户也可以手动编辑配置文件。

---

## 频道颜色

工具支持根据聊天频道显示不同颜色，颜色可以在设置界面中自定义。

| 频道 | 默认颜色 | HEX |
|---|---|---|
| 🟦 队伍 / Team | 浅蓝色 | `#4FC3F7` |
| 🟧 所有人 / All | 橙色 | `#FFB74D` |
| 🟪 小队 / Party | 紫色 | `#CE93D8` |
| ⬜ 未知 / Unknown | 灰色 | `#B0BEC5` |
| 🟨 系统消息 / System | 黄色 | `#FFD54F` |

---

## 多语言频道识别

为了适配各个玩家的客户端语言，工具支持多语言频道标签，例如：

| 频道 | 示例 |
|---|---|
| 队伍 | `[队伍]`、`[Team]`、`[チーム]`、`[팀]` |
| 所有人 | `[所有人]`、`[All]`、`[全員]`、`[전체]` |
| 小队 | `[小队]`、`[Party]`、`[パーティー]`、`[파티]` |

如果 OCR 把频道识别错，工具也会尝试用别名表和模糊匹配进行修正。

---

## 项目状态

当前项目仍处于开发阶段，可能存在：

- OCR 识别不稳定
- 部分语言频道识别不完整
- 部分游戏黑话未覆盖
- 不同分辨率 / UI 缩放下需要手动调整截图区域

欢迎提交 Issue 或 Pull Request 来补充术语、频道别名和 OCR 修正规则。

---

## 免责声明

League of Legends 是 Riot Games 的商标或注册商标。  
本项目与 Riot Games 没有任何关联，也不是 Riot Games 官方认可、赞助或维护的工具。

本项目仅用于辅助理解游戏聊天内容。  
请自行遵守游戏服务条款，并避免将本工具用于任何自动化、作弊、骚扰或破坏游戏体验的行为。

---

## English Summary

LoL Chat Translator is an OCR-based real-time chat translator overlay for League of Legends.

It captures the in-game chat area, recognizes messages with OCR, normalizes common LoL slang, translates the text, and displays it in a customizable overlay.

This project does not hook, inject, read memory, capture packets, or send chat messages automatically. It only uses screen capture, OCR, text normalization, translation, and overlay display.
