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
- 人聲增厚 EQ，補足偏薄的人聲並讓聲音更靠前
- Podcast 響度標準化，讓分段錄音音量更一致
- 防爆音 Limiter，降低突然大聲造成的爆音風險
- 音量增益調整
- 播放原始音檔與處理後音檔
- 處理時顯示進度視窗、目前檔名與百分比
- 可中途取消處理
- 自動將輸出檔存到指定資料夾
- 可指定 AI 處理後音檔資料夾，批次配對 AI 音檔後再進行後製
- 可為每個音檔設定剪輯開始與結束時間
- 可將多段錄音依清單順序統整成單一音檔
- 可設定段落間空白秒數與統整輸出檔名
- 可選擇輸出格式：M4A / AAC、MP3、WAV
- 統整輸出可加入片頭、片尾，並設定淡入淡出秒數
- 統整輸出可寫入 Podcast 標題、作者、專輯名稱與封面圖片

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
4. 如果已有 AI 工具處理後的音檔，點選「AI 音檔資料夾」並勾選「使用 AI 處理後音檔」。
5. 調整靜音秒數、靜音門檻、降噪、減少空間音、人聲增厚 EQ、響度標準化、防爆音、音量增益與輸出格式。
6. 如需剪輯，可在清單中的「開始」「結束」欄位輸入時間。
7. 如需改檔名，可在清單中的「輸出檔名」欄位輸入新檔名。
8. 如需合併多段錄音，可用「上移」「下移」調整清單順序，並勾選「統整成單一檔案」。
9. 統整輸出時可視需要填寫標題、作者、節目名稱，並選擇封面圖片。
10. 可視需要選擇片頭、片尾，並調整淡入淡出秒數。
11. 點選「處理選取」或「全部輸出」。
12. 處理時可在進度視窗查看目前檔案與百分比，也可按「取消」中止。

剪輯時間可輸入秒數或時間格式，例如：

```text
12.5
01:23
1:02:03
```

「開始」留空代表從音檔開頭處理，「結束」留空代表處理到音檔結尾。

## AI 前處理音檔

程式目前採用 AI 外掛模式，不會內建或執行 AI 模型。你可以先用 Adobe Podcast Enhance、Auphonic、DaVinci Resolve Voice Isolation 或其他 AI 工具處理音檔，再把輸出檔放到 AI 音檔資料夾。

程式會依原始檔名自動尋找以下格式：

```text
原檔名.wav
原檔名_enhanced.wav
原檔名-enhanced.wav
原檔名_cleaned.wav
原檔名-cleaned.wav
原檔名_ai.wav
原檔名-ai.wav
```

副檔名可不同，只要是支援的音檔格式即可。找不到 AI 版本時，程式會改用原始音檔繼續處理。

## 統整輸出

統整輸出模式會依照清單順序處理多段錄音，先把每段套用目前的後製設定，再合併成單一音檔。

可設定：

- 段落間空白秒數
- 輸出格式
- 統整輸出檔名
- 每段音檔的剪輯開始與結束時間
- Podcast 標題、作者 / 主持人、節目 / 專輯名稱
- 封面圖片
- 片頭音檔與片頭淡入秒數
- 片尾音檔與片尾淡出秒數
- 是否使用 AI 處理後音檔
- 降噪、減少空間音、人聲增厚 EQ、響度標準化與 Limiter

統整輸出順序為：片頭、清單中的分段音檔、片尾。片頭與片尾會先轉成合併用暫存音檔，再與分段錄音一起輸出成單一檔案。

封面圖片會在最後合併輸出時嵌入音檔。建議使用正方形 JPG 或 PNG，例如 1400 x 1400 或 3000 x 3000。WAV 格式主要用於無壓縮音訊，封面相容性較差，程式會略過封面嵌入。

如果沒有輸入副檔名，程式會依目前選擇的輸出格式自動補上副檔名。

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

如果在「輸出檔名」欄位輸入自訂名稱，程式會使用該名稱輸出。副檔名會依目前選擇的輸出格式套用。

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

- 長音檔搭配降噪、減少空間音、人聲增厚 EQ 與響度標準化會需要較長處理時間。
- 程式會限制 FFmpeg 執行緒數量並降低 FFmpeg 優先權，避免處理長音檔時 UI 過度卡住。
- `processed/`、`bin/`、`obj/`、`.build_*` 等輸出資料夾已在 `.gitignore` 中排除。
