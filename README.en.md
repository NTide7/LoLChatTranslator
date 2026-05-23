# LOL Chat OCR Translator (DEV)

English | [简体中文](README.zh-CN.md)

> A safe screenshot-based OCR chat translation helper for League of Legends.  
> Select the in-game chat area, recognize chat text with OCR, clean and deduplicate the result, translate it with League-aware terminology, and show it in a standalone overlay.

![Windows](https://img.shields.io/badge/Windows-10%2F11-blue)
![.NET](https://img.shields.io/badge/.NET-8.0-purple)
![WPF](https://img.shields.io/badge/UI-WPF-0ea5e9)
![OCR](https://img.shields.io/badge/OCR-Windows%20OCR%20%7C%20PP--OCRv5-22c55e)
![Status](https://img.shields.io/badge/status-in%20development-f59e0b)

## Overview

LOL Chat OCR Translator is a Windows desktop app that helps you understand and reply to League of Legends in-game chat. It does not depend on an official chat API and does not read game memory. Instead, it captures only the user-selected chat region, runs OCR, post-processes the recognized chat lines, translates them, and displays the result in a separate overlay window.

It is useful when you want to:

- Understand teammates in multilingual regions such as Taiwan, Southeast Asia, Korea, Japan, or mixed-language games.
- Translate short player messages, League slang, and tactical calls into natural local expressions.
- Translate your own replies without letting the tool automatically send anything.
- Use a non-intrusive helper that avoids injection, hooks, memory reading, and gameplay automation.

> ⚠️ This project is not affiliated with Riot Games or League of Legends. It only performs screenshot OCR, translation, and overlay display. It does not provide automation, auto-chat, scripting, or gameplay advantage features.

## Main Features

### 1. Screenshot OCR for in-game chat

- Select the exact chat region manually.
- Preview the current OCR region to verify the selected area.
- Run automatic recognition or a one-time manual recognition.
- Use either Windows OCR or PP-OCRv5 multilingual OCR.
- Supports auto, Chinese + English, English, Japanese, and advanced multilingual OCR options.
- Supports stable, balanced, and fast automatic recognition presets.
- PP-OCRv5 dependencies can be detected and installed from inside the app. The app creates a dedicated project virtual environment by default and does not write to the user’s global Python environment.

### 2. Chat translation

- Auto-detect source language.
- Translate into Simplified Chinese, Traditional Chinese, English, Korean, Japanese, and Vietnamese.
- Use MyMemory free translation without an API key.
- Use AI API / OpenAI-compatible endpoints with configurable API Base, API Key, and model name.
- Use local Ollama models for users who prefer local translation.
- Built-in translation cache reduces repeated requests.
- Suspicious untranslated, partially translated, or provider-error outputs are validated and skipped to avoid noisy overlay results.

### 3. Standalone overlay window

- Translation results are shown in a separate overlay without modifying the game client.
- Configure always-on-top, opacity, font size, and maximum visible lines.
- Show or hide sender names.
- Customize colors for team, all-chat, party, system, and unknown channels.
- Enable click-through mode.
- Hide or exclude the overlay during OCR capture to prevent the app from recognizing its own translated text.

### 4. Reply translation to clipboard

- The main window includes a reply translation input box.
- Translated replies are copied to the clipboard. The user manually pastes and sends them in game.
- The overlay can also show an input box for quick reply translation.
- Overlay input can automatically reverse-translate to the detected source language of the latest OCR chat, or use a manually selected target language.

### 5. Filtering and player exclusion

- Optionally remove usernames from translation input.
- Optionally hide channel tags in the overlay.
- Filter system messages, ping messages, kill messages, and purchase messages.
- Exclude up to 50 Riot IDs.
- Excluded players are skipped before deduplication, glossary matching, and translation.

### 6. Hotkeys

Default hotkeys:

| Action | Default hotkey |
| --- | --- |
| Recognize once | `F8` |
| Toggle auto translation | `F9` |
| Translate clipboard text | `Ctrl + Shift + T` |
| Open settings | `Ctrl + Shift + S` |
| Reselect chat region | `Ctrl + Shift + R` |
| Preview current region | `Ctrl + Shift + P` |
| Show / hide overlay | `Ctrl + Shift + H` |
| Focus overlay input | `Ctrl + Shift + I` |

Hotkeys can be changed or cleared in settings.

### 7. Installation, updates, and environment management

- Includes a WinForms installer with custom install directory and optional desktop shortcut.
- Registers a Windows uninstall entry for easy removal from system settings.
- The About page can check the latest GitHub Releases version.
- The dedicated PP-OCRv5 OCR environment can be deleted from inside the app without affecting the user’s own Python installation.

## Special Features

### League-aware post-processing

This project is more than a generic OCR-to-translator pipeline. It includes League-specific cleanup, fixes, and terminology handling:

- Built-in League slang, common phrases, short calls, and toxicity label resources.
- Local direct translations for common gank requests such as `pls gank mid`, `plsgankmid`, and `gank mid pls`.
- Common League terms such as `ff`, `gg`, `ult`, `tp`, `ward`, `drake`, and `baron` are handled more carefully.
- OCR-glued English text can be repaired, for example `ilikebanana` and `okilikechinesetoo`.
- Channel OCR typos are tolerated for team, all-chat, and party channels.

### Reading order and multi-line chat handling

OCR may return text in the wrong order or split a single message into multiple fragments. The app post-processes OCR results by:

- Sorting OCR lines by bounding boxes to recover top-to-bottom, left-to-right reading order.
- Merging wrapped chat lines when they are likely part of the same player message.
- Blocking unsafe merges around system messages, practice tool notices, timestamps, and standalone short messages.
- Stabilizing long messages to reduce “partial line first, full line later” output.

### Deduplication and stable output

Because the same chat line stays visible across many frames, repeated OCR results must be controlled. The app includes multiple deduplication layers:

- Deduplication by timestamp, sender, and normalized message body.
- Deduplication for chat without timestamps.
- Deduplication is committed only after successful translation, so failed translations can still retry.
- Multiple candidate messages from the same OCR frame are output in source order.

### Live Client Data assistance

The app attempts to read the local League Live Client Data API to improve current-game context:

- Better sender matching.
- Champion name, title, and alias enrichment.
- Better player exclusion, channel parsing, and overlay display.

If Live Client Data is unavailable, the app continues using OCR text only.

### Toxic content display policy

Abusive or aggressive expressions are handled locally by the built-in glossary before normal translation providers are used. Users can choose how such content is displayed:

- Safe label, for example `[severe abuse: family attack]`.
- Literal meaning, which may reveal the actual insult.
- Original OCR text, which shows only the recognized source characters.
- Hidden / generic abuse notice.

### OCR testing and diagnostics

The app includes an OCR test window for troubleshooting slow or inaccurate recognition:

- View the captured region image.
- View the actual image sent to OCR.
- Inspect OCR timing, model information, confidence, and bounding boxes.
- Run preprocessing comparisons such as original image, grayscale, contrast enhancement, and binarization.
- Save detailed logs and debug images when diagnostics are enabled.

## Screenshots

> Add screenshots before publishing a polished release: main window, overlay, settings window, and OCR test window.  
> Recommended path: `docs/images/`.

## Installation and Usage

### Recommended: use the installer

1. Download the latest installer from GitHub Releases.
2. Run `LoLChatTranslator_Setup_x.x.x.exe`.
3. Choose an install directory and optionally create a desktop shortcut.
4. Start the app and click “Select Chat Region”.
5. If using PP-OCRv5, open settings and click “Detect / Install PP-OCRv5 OCR Environment”.
6. Choose the translation engine and target language.
7. Click “Start Auto”.

### OCR engine recommendation

| Engine | Strength | Best for |
| --- | --- | --- |
| Windows OCR | Built in, simple setup | Quick tests and lightweight use |
| PP-OCRv5 multilingual | Better accuracy and more languages | Long-term use, multilingual games, complex chat backgrounds |

## Run from Source

### Requirements

- Windows 10 / Windows 11 x64.
- .NET 8 SDK.
- Optional: Python 3.10 - 3.12 x64 for PP-OCRv5.
- Optional: Ollama for local AI translation.

> Because the project uses WPF and Windows OCR APIs, it should be built and run on Windows.

### Build

```powershell
git clone https://github.com/NTide7/LoLChatTranslator.git
cd LoLChatTranslator

dotnet restore
dotnet build .\LoLChatTranslator.sln -c Release
```

### Run

```powershell
dotnet run --project .\LoLChatTranslator\LoLChatTranslator.csproj
```

### Test

```powershell
dotnet test .\LoLChatTranslator.Tests\LoLChatTranslator.Tests.csproj
```

### Build installer

```powershell
powershell -ExecutionPolicy Bypass -File .\Installer\build-installer.ps1
```

The script generates:

- `Installer\dist\LoLChatTranslator_Setup_1.0.0.exe`
- `Installer\dist\LoLChatTranslator_FrameworkDependent_1.0.0_win-x64.zip`

## Project Structure

```text
LoLChatTranslator/
├─ LoLChatTranslator/                 # WPF desktop app
│  ├─ MainWindow.xaml(.cs)             # Main window, automatic OCR, translation pipeline
│  ├─ OverlayWindow.xaml(.cs)          # Overlay window
│  ├─ SettingsWindow.xaml(.cs)         # Settings window
│  ├─ OcrTestWindow.xaml(.cs)          # OCR test window
│  ├─ OCR/ppocrv5_multilingual.py      # PP-OCRv5 Python worker
│  ├─ Resources/                       # Slang, glossary, and channel alias resources
│  └─ Services/                        # OCR, translation, cleanup, dedupe, logging, and helpers
├─ LoLChatTranslator.Tests/            # xUnit regression tests
├─ Installer/                          # WinForms installer and packaging script
├─ Tools/                              # Utility scripts
└─ LoLChatTranslator.sln
```

## Privacy and Safety

- The app captures only the screen region selected by the user.
- It does not inject into the game process.
- It does not read game memory.
- It does not automatically send chat messages.
- Reply translation only copies text to the clipboard; the user must paste and send manually.
- MyMemory and AI API translation send translated text to the selected provider.
- Ollama can keep translation requests local.
- Toxic content, selected short phrases, and glossary matches are handled locally first.

## Current Status

The project is still in development. OCR speed, recognition accuracy, unusual resolution scaling, and multilingual terminology coverage may still need improvement. Repro screenshots, logs, and issues are welcome.

## Acknowledgements

- Windows OCR / Windows.Media.Ocr
- PaddleOCR / PP-OCRv5
- MyMemory Translation
- Ollama
- Riot Data Dragon

## License

No LICENSE file is included in the current source package. Please add an explicit license before public distribution or external reuse.
