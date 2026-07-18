# 音檔批次調整工具

這是一個以 .NET 8 製作的音檔批次處理工具，主要用來批次移除靜音片段、降噪、減少空間音，並可在處理前後播放比對音檔。

專案包含兩個版本：

- `PodcastBatchCleanerWpf.csproj`：Windows WPF 版本
- `PodcastBatchCleaner.CrossPlatform/PodcastBatchCleaner.CrossPlatform.csproj`：Avalonia 跨平台版本，目標為 Windows、macOS、Linux

也可以直接開啟 `PodcastBatchCleaner.sln` 檢視完整方案。

## 功能

- 批次載入資料夾內的音檔
- 支援處理單一選取檔案或全部檔案
- 可為每個音檔自訂輸出檔名
- 移除超過指定秒數的靜音片段
- 可調整靜音判定門檻 dBFS
- 統一降噪
- 減少空間音，降低房間感與低中頻箱音
- 音量增益調整
- 播放原始音檔與處理後音檔
- 處理時顯示進度視窗、目前檔名與百分比
- 可中途取消處理
- 自動將輸出檔存到指定資料夾

## 支援格式

目前支援以下副檔名：

- `.mp3`
- `.wav`
- `.m4a`
- `.aac`
- `.flac`
- `.ogg`
- `.wma`
- `.mp4`

## 系統需求

- .NET 8 SDK
- FFmpeg

Windows WPF 版本需要 `ffmpeg.exe`。程式會嘗試自動尋找 FFmpeg，也可以在介面中手動指定。

Avalonia 跨平台版本會依平台尋找 `ffmpeg` / `ffmpeg.exe`，播放功能另需要 `ffplay` / `ffplay.exe`。

## 安裝 FFmpeg

Windows 可下載 FFmpeg 後，將 `ffmpeg.exe` 放到：

- 系統 PATH
- 程式輸出資料夾
- 程式輸出資料夾下的 `tools/`

macOS 可使用 Homebrew：

```bash
brew install ffmpeg
```

Linux 可使用系統套件管理器，例如 Ubuntu / Debian：

```bash
sudo apt install ffmpeg
```

## 執行方式

Windows WPF 版本：

```powershell
dotnet run --project .\PodcastBatchCleanerWpf.csproj
```

Avalonia 跨平台版本：

```powershell
dotnet run --project .\PodcastBatchCleaner.CrossPlatform\PodcastBatchCleaner.CrossPlatform.csproj
```

macOS / Linux：

```bash
dotnet run --project ./PodcastBatchCleaner.CrossPlatform/PodcastBatchCleaner.CrossPlatform.csproj
```

## 編譯

完整方案：

```powershell
dotnet build .\PodcastBatchCleaner.sln
```

Windows WPF 版本：

```powershell
dotnet build .\PodcastBatchCleanerWpf.csproj
```

Avalonia 跨平台版本：

```powershell
dotnet build .\PodcastBatchCleaner.CrossPlatform\PodcastBatchCleaner.CrossPlatform.csproj
```

## 使用方式

1. 點選「選取資料夾」，載入要處理的音檔。
2. 視需要點選「輸出位置」指定輸出資料夾。
3. 如果程式沒有自動找到 FFmpeg，點選「指定 FFmpeg」選取 `ffmpeg.exe`。
4. 調整靜音秒數、靜音門檻、降噪、減少空間音與音量增益。
5. 如需改檔名，可在清單中的「輸出檔名」欄位輸入新檔名。
6. 點選「處理選取」或「全部輸出」。
7. 處理時可在進度視窗查看目前檔案與百分比，也可按「取消」中止。

## 輸出檔案

處理後的檔案會以原檔名加上 `_processed` 輸出，例如：

```text
example.m4a
example_processed.m4a
```

如果輸出檔已存在，程式會自動加上編號避免覆蓋：

```text
example_processed_2.m4a
```

如果在「輸出檔名」欄位輸入自訂名稱，程式會使用該名稱輸出。沒有輸入副檔名時，會自動沿用原音檔副檔名。

## 專案結構

```text
.
├── Models/
├── Services/
│   └── FfmpegAudioProcessor.cs
├── PodcastBatchCleaner.CrossPlatform/
├── App.xaml
├── MainWindow.xaml
├── ProcessingProgressWindow.xaml
├── PodcastBatchCleaner.sln
├── PodcastBatchCleanerWpf.csproj
└── README.md
```

`Models/` 與 `Services/` 使用 `PodcastBatchCleaner.Core` namespace，供 WPF 與 Avalonia 版本共用。

## 注意事項

- 長音檔搭配降噪與減少空間音會需要較長處理時間。
- 程式會限制 FFmpeg 執行緒數量並降低 FFmpeg 優先權，避免處理長音檔時 UI 過度卡住。
- `processed/`、`bin/`、`obj/`、`.build_*` 等輸出資料夾已在 `.gitignore` 中排除。
