# 音訊工作台 / PodcastBatchCleaner

以 .NET 10 製作的桌面音檔批次後製工具。選取資料夾後，可剪除空拍、降噪、調整人聲與響度，直接試聽處理結果，再輸出單檔或合併整集。音訊處理由本機 FFmpeg 與選配 AI 工具執行，不需要改用網頁。

## 版本與平台

| 版本 | 專案／發布目標 | 驗證狀態 |
| --- | --- | --- |
| Windows WPF | `PodcastBatchCleanerWpf.csproj` | 已在 Windows 建置並執行回歸測試 |
| Avalonia 桌面版 | `PodcastBatchCleaner.CrossPlatform/PodcastBatchCleaner.CrossPlatform.csproj` | 已在 Windows 建置、測試介面與實際音訊輸出 |
| macOS Apple Silicon / Intel | `osx-arm64` / `osx-x64` | 已產生 app bundle，尚待 Mac 實機驗收；未簽章／公證 |
| Linux x64 | `linux-x64` | 提供發布目標，尚待 Linux 實機驗收 |

兩版共用處理流程、模型與服務，選檔、播放器、波形及對話框由各平台實作。跨平台不代表所有平台均已驗證完成。

儲存庫提供原始碼與發布腳本，**不包含編譯成品、個人音檔、AI 模型或第三方工具執行檔**。以下可自行建置，不保證已有 GitHub Release 安裝包。

## 功能與操作

- 資料夾清單、選取／全部輸出、上移／下移及拖曳排序。
- 每檔剪輯起訖時間、自訂輸出名稱。
- 0.1 秒單位空拍剪除、靜音門檻與剪接保留時間。
- FFmpeg 降噪、空間音／箱音抑制、殘響／尾音抑制及進階清理。
- 人聲增厚、清晰度、輕度壓縮、三頻 EQ 與齒音頻帶削減。
- dB 增益、LUFS 標準化、Limiter 與編碼後真峰值驗證。
- 原音／處理後播放、暫停、停止、時間軸、30 秒試聽、A/B 與單項效果比較。
- 原音波形、游標剪輯、品質分析、整集合併與段落驗收。
- 選配 DeepFilterNet、VoiceFixer 或外部 AI 音檔。
- 工作檔、背景草稿、未保存提醒、成品過期提示及輸出前確認。

### 操作流程

1. 選取資料夾，從清單選取音檔。清單顯示「音檔、長度、狀態」。
2. 在清單下方修改該檔開始／結束時間與輸出名稱；時間可輸入秒數或 `mm:ss`。多選時只編輯目前聚焦的檔案，不會批次覆寫。
3. 在「快速處理」套用模板、調整空拍／音量／響度；在「AI / 修復」設定清理、人聲與音色。
4. 使用底部固定播放器旁的「30 秒試聽」或「A/B 比較」確認效果。
5. 在「輸出設定」選格式、目的地與合併方式。工具列會顯示「輸出選取／輸出全部」或「合併選取／合併全部」。
6. 確認輸出摘要後執行，可中途取消。完成後播放結果或開啟輸出位置。
7. 用頂部「保存工作」保留清單、剪輯與設定，下次以「恢復工作」繼續。

「波形分析」提供原音波形、品質分析、缺失來源提示與整集成品驗收。點波形會定位並切回原音，可用游標設定剪輯範圍。剪除空拍後，原音與成品的時間軸可能不同。

介面為黑色系、圓角控制項與平面工作區，左右面板可拖曳調寬；進階設定預設收合，人聲／EQ／齒音細調於啟用後顯示。播放器固定在底部，不受目前頁籤影響。

### 支援格式

輸入：`.mp3`、`.wav`、`.m4a`、`.aac`、`.flac`、`.ogg`、`.wma`、`.mp4`，實際解碼能力依 FFmpeg 建置及來源檔案而定。

輸出：**M4A / AAC、MP3、WAV（16-bit PCM）**，預設 48 kHz。MP3 需要 `libmp3lame`；缺少編碼器時依程式提示處理。

## 快速開始

### 環境與 FFmpeg

從原始碼執行需要 **.NET 10 SDK** 與 **FFmpeg**。Windows 可將工具放在專案 `tools/FFmpeg/` 或加入 PATH，介面也可指定 FFmpeg：

```text
tools/FFmpeg/ffmpeg.exe
tools/FFmpeg/ffprobe.exe
tools/FFmpeg/ffplay.exe
```

Avalonia 播放另需 `ffplay`；`ffprobe` 用於時長讀取，無法使用時嘗試以 FFmpeg 備援。FFmpeg 需支援 `adynamicequalizer`、`agate` 及響度處理等濾鏡，精簡建置可能不完整。本機工具來源與授權記錄見 [tools/FFmpeg/README.md](tools/FFmpeg/README.md)，執行檔不納入 Git。

macOS（先安裝 Homebrew）：

```bash
brew install ffmpeg
```

Ubuntu / Debian：

```bash
sudo apt install ffmpeg
```

Mac 搜尋包含 `/opt/homebrew/bin` 與 `/usr/local/bin`，避免 Finder 啟動時 PATH 不完整。

### 執行

```bash
git clone https://github.com/kevinyao1107/PodcastBatchCleaner.git
cd PodcastBatchCleaner
```

Windows WPF：

```powershell
dotnet run --project ./PodcastBatchCleanerWpf.csproj
```

Avalonia（Windows / macOS / Linux）：

```bash
dotnet run --project ./PodcastBatchCleaner.CrossPlatform/PodcastBatchCleaner.CrossPlatform.csproj
```

Mac／Linux 請使用 Avalonia 專案，不要建置含 WPF 的完整方案。

## 聲音設定

### 空拍與音量的差別

| 設定 | 意義 |
| --- | --- |
| 空拍秒數 | 低於靜音門檻且持續達設定時間的停頓才進入剪除判定。0.1～10 秒，步進 0.1，例如 0.1、0.2、0.3 |
| 靜音判定 dBFS | 決定多小聲算靜音。越負門檻越低，-50 dBFS 比 -35 dBFS 更不容易將小聲語音當靜音 |
| 前後保留時間 | 保留剪接邊界，降低傷害呼吸與尾字的機會 |
| 音量增益 dB | 整體放大／縮小；0 不變、正值變大、負值變小，不等於靜音門檻 |
| 響度目標 LUFS | 朝 -14、-16、-18 或 -23 LUFS 調整整體響度，越負目標越小聲 |

增益在響度標準化之前套用；啟用標準化後，最終音量主要取決於目標與峰值保護，不保證增益加多少成品就大多少。

空拍秒數不要求每段語音也持續同樣長度。剪輯終點先限制輸入，再套用改變時長的效果，避免讀入終點以後的聲音。

### 清理細調

需開啟「使用細調參數」，才會取代相應的基本模式設定。

| 項目 | 範圍 |
| --- | --- |
| 傳統降噪量 | 1～30 dB |
| 自動追蹤底噪 | 開／關 |
| 噪音底線 | -70～-20 dBFS |
| 空間音低切 | 40～120 Hz |
| 箱音抑制中心 | 150～600 Hz |
| 箱音最大削減 | 2～12 dB |
| 尾音衰減門檻 | -60～-25 dBFS |
| 尾音釋放 | 100～800 ms |

空間音／尾音使用高通、動態 EQ 與 Gate／擴展器改善聽感，**不是專用神經網路去殘響，不保證完整分離房間反射**。釋放時間是衰減速度，不是剪除長度；過強可能傷害尾字，應先試聽。

### 人聲與音色

- 人聲厚度：160 Hz，0～6 dB，搭配低中頻修整，不改變音高。
- 清晰度：3200 Hz，0～4 dB。
- 輕度壓縮可獨立勾選；兩項 EQ 為 0 可只使用壓縮，總開關關閉則整組不套用。
- 預設厚度 2 dB、清晰度 1 dB、開啟壓縮，維持舊版預設音色。
- 三頻 EQ：140 Hz 低架、1.1 kHz 中頻、8.5 kHz 高架。
- 齒音抑制：7.2 kHz 頻帶削減，100% 約 -9 dB，**不是動態 De-esser**。

### 三種試聽

| 方式 | 範圍 |
| --- | --- |
| 30 秒試聽 | 從剪輯起點產生最長 30 秒的目前設定結果 |
| A/B 比較 | 原音、目前設定與不同模板，使用所選剪輯範圍；可匹配 -16 LUFS，不固定限制 30 秒 |
| 單項效果比較 | FFmpeg 降噪、空間音／箱音、殘響／尾音或人聲增厚，產生同段最長 30 秒的旁通與效果 WAV，固定匹配 -16 LUFS |

單項比較不使用 AI、不剪空拍、不套正式增益或其他清理；驗證峰值，全靜音維持靜音。切換對照可保留比較內的位置；來源／剪輯／參數改變後舊比較不可繼續播放。取消保留先前有效比較，不取代正式成品，檔案位於 `effect_preview/`。

Avalonia 使用 FFplay 子程序，暫停保留位置並停止播放器，恢復／定位從該處重新啟動；時間為時鐘估計，不是樣本精確的硬體定位。

## 選配 AI

工具與模型另裝，不隨程式發布；未啟用 AI 仍可使用 FFmpeg 後製。

- **DeepFilterNet**：指定 `deep-filter.exe`（Windows）或 `deep-filter`，可放 `tools/DeepFilterNet/` 供搜尋，支援 PostFilter。成功後略過重複 FFmpeg 降噪。
- **VoiceFixer**：指定可執行工具／啟動腳本，可放 `tools/VoiceFixer/`。僅安裝 Python 套件不等於已具備相容啟動腳本。0% 混合略過模型，100% 為純修復，中間比例先對齊時間／音量；無法可靠混合時退回純修復並警告。
- **外部 AI 資料夾**：精確配對原始檔名，接受原名或 `_enhanced`、`-enhanced`、`_cleaned`、`-cleaned`、`_ai`、`-ai` 後綴及支援的副檔名。缺失退回原音並提示，多個候選拒絕配對，不用相似前綴猜測。

保持立體聲時可能需分別處理左右聲道再合回，耗時較長；較短側補靜音保留另一側尾段，不代表可修正模型左右時間漂移。另提供置中、Mono、指定左／右、平衡與互換等聲道模式。

啟用 AI 失敗備援時可回到 FFmpeg；失敗、配對缺失與混合退回會顯示警告／已完成階段，仍需確認聽感。Mac／Linux 需對應 OS／CPU 的工具，不能使用 Windows `.exe`／`.cmd`／`.bat`，AI 跨平台實機部署尚待驗收。

## 輸出與整集驗收

正式輸出前摘要包含檔數、格式、聲道、目的地、合併方式及效果。取消不建立輸出資料夾、不啟動處理；確認期間設定／來源改變需重新確認。

預設 `原檔名_processed`，可自訂名稱，副檔名依格式。同名自動加序號，不覆蓋原音。預設 `processed` 隨來源資料夾切換，手動指定的其他輸出位置保留。

合併順序為「片頭 → 清單段落 → 片尾」，支援間隔、片頭淡入、片尾淡出、標題、作者、專輯及封面；WAV 略過封面嵌入，播放器的標籤支援可能不同。整集標準化包含附件與空白，朝**所選 LUFS 目標**處理，不自動平衡音樂與人聲相對音量。

啟用 Limiter 或標準化時會解碼驗證真峰值上限 **-1.5 dBTP**；超標最多三次從原始輸入重做並降低最終增益，不反覆壓縮已編碼成品。驗證失敗／取消不發布半成品，保留舊輸出。峰值保護可能使響度低於目標；兩者都關閉時一般輸出不強制限制峰值。驗證會增加解碼時間。

「波形分析 → 整集成品驗收」可播放並點選段落定位，時間依實際處理後片段長度計算。

## 工作保存與保護

### 工作檔

「保存工作／恢復工作」使用 `*.podcast-work.json`，格式 `podcast-cleaner-work` v1，最多 2000 檔、2 MB。

保存清單順序、來源路徑、剪輯、名稱、音效、輸出位置、整集附件與資訊，**不含音訊本體、成品或播放位置**。相對路徑可隨資料夾樹搬移；其他絕對路徑可能需重選。工具執行檔路徑與命名預設庫沿用本機設定，不由工作檔覆蓋。

恢復前驗證整份內容。缺失來源保留於清單，以「重新指定選取音檔」修復，可保留剪輯與名稱；附件另行重選。恢復後需重新產生成品。

### 未保存與草稿

- 標題 `*` 與底部工作狀態表示未保存。
- 換資料夾／恢復另一工作前提供保存、放棄、取消；關閉另可保留草稿。保存失敗、取消選檔或關閉對話框不會繼續關閉。
- 有未保存變更且閒置時，每 2 秒檢查並原子寫入草稿；開始處理／分析前也先嘗試保存。失敗顯示狀態，不冒充已保存。
- 草稿在設定目錄 `drafts/`，不覆蓋手動工作檔、不含音訊。設定為 `LocalApplicationData/PodcastBatchCleaner/settings.json`，實際根目錄依 OS 而異。
- 啟動可恢復有效且未被其他視窗使用的草稿，之後仍需手動保存；損壞草稿保留。
- 手動保存清除自己的舊草稿；放棄關閉不將放棄的設定另存為常用設定。
- 草稿不是音檔備份；中斷仍可能遺失尚未寫入的編輯，包括最近約 2 秒或草稿寫入失敗期間的變更。

### 成品過期提示

來源、剪輯、音效或輸出設定改變後，舊結果仍保留可播放，但會提醒不符合目前設定。完整輸出、30 秒試聽、A/B 目前設定分開標示；整集也檢查順序、附件與資訊。無來源紀錄的成品不宣稱符合目前設定，提醒不會自動重新輸出。

## 共用預設與 SoundLab

可與 [soundlab-audio-studio](https://github.com/kevinyao1107/soundlab-audio-studio) 交換 `audio-studio-presets` JSON，匯出 v2、匯入相容 v1／v2，最多 100 組、1 MB。

核心包含增益（-12～12 dB）、響度目標／開關、靜音門檻（-70～-25 dBFS）、空拍（0.1～10 秒）、保留時間（0～0.5 秒）、可對應聲道、三頻 EQ 與齒音頻帶削減。超出共用範圍會要求調整，不暗中截值。

桌面專用音色／清理存於 `extensions.desktop`；AI、路徑、剪輯與整集資訊不在共用預設中。跨工具不支援的效果會停用並提示。**設定相容不代表兩套程式有完全相同功能、音色或剪接點**。

## 建置與發布

Windows 完整方案：

```powershell
dotnet build ./PodcastBatchCleaner.sln -c Release -m:1 -p:UseSharedCompilation=false
```

WPF 成品為 `bin/Release/net10.0-windows/PodcastBatchCleanerWpf.exe`，需 .NET 10 Desktop Runtime 並保留同目錄 DLL，不是獨立安裝包。

跨平台只建置 Avalonia：

```bash
dotnet build ./PodcastBatchCleaner.CrossPlatform/PodcastBatchCleaner.CrossPlatform.csproj -c Release
```

PowerShell 7 + .NET 10 SDK 發布自帶執行階段版本：

```powershell
./scripts/Publish-Desktop.ps1 -Runtime win-x64
./scripts/Publish-Desktop.ps1 -Runtime osx-arm64
./scripts/Publish-Desktop.ps1 -Runtime osx-x64
./scripts/Publish-Desktop.ps1 -Runtime linux-x64
```

輸出於 `release/Avalonia/<Runtime>/`，不需另裝 .NET，但仍需系統桌面相依套件與 FFmpeg。

- Windows 執行 `PodcastBatchCleaner.CrossPlatform.exe`，保留整個資料夾。腳本只複製本機已存在的 FFmpeg 與授權，不自動下載。
- Mac：Apple Silicon 用 `osx-arm64`、Intel 用 `osx-x64`，執行產生的 `PodcastBatchCleaner.app`；另裝 Mac FFmpeg／AI。
- Linux：`linux-x64` 需桌面環境與相依套件，目前尚未實機驗收。

Windows 產生的 Mac app 搬移後可能需補權限：

```bash
chmod +x PodcastBatchCleaner.app/Contents/MacOS/PodcastBatchCleaner.CrossPlatform
```

Mac 未簽章／公證，可能受 Gatekeeper 限制，尚非已驗收的 DMG。不要將 Windows 工具當成 Mac 相依檔案。

## 測試

Windows 回歸命令，工具路徑依本機調整：

```powershell
$env:DOTNET_PROCESSOR_COUNT = '2'
dotnet run --project ./Tests/CrossPlatformChecks.csproj -p:UseSharedCompilation=false -- .build_cross_ui tools/FFmpeg/ffmpeg.exe
dotnet run --project ./Tests/RegressionChecks.csproj -p:UseSharedCompilation=false -- tools/FFmpeg/ffmpeg.exe tools/DeepFilterNet/deep-filter.exe .build_wpf_ui
```

WPF 可只傳 FFmpeg 路徑，第二個參數測 DeepFilterNet，第三個可指定 UI 檢查圖目錄。缺少編碼器的案例列為 SKIP，不算成功驗證。

涵蓋實際剪輯／靜音／音色／峰值／合併、AI 配對與備援、取消清理、工作檔／預設、波形／播放器、980 × 640 與 1180 × 760 介面，以及草稿隔離／恢復、關閉取消、保存失敗、輸出確認與成品比對。

測試使用隔離設定，Avalonia 播放檢查使用虛擬音訊輸出。兩套回歸與 Release 建置已在 Windows 通過，不代表已驗證 Mac 聲音輸出、原生選檔、AI 安裝或真實錄音聽感。

另提供長錄音壓力模式，需至少 30 分鐘來源及 DeepFilterNet：

```powershell
dotnet run --project ./Tests/RegressionChecks.csproj -- --long-audio "D:/recording.m4a" "C:/tools/ffmpeg/bin/ffmpeg.exe" "C:/tools/DeepFilterNet/deep-filter.exe" ".build_long_audio_check"
```

輸出隔離報告與成品，檢查處理、取消、Dispatcher 回應與原檔雜湊；使用前需預留暫存空間，不代表完成聽感驗收。

## 專案結構

```text
MainWindow.xaml / MainWindow.xaml.cs     WPF 畫面與共用工作流程
Desktop/                                共用工作保護與對話框
PodcastBatchCleaner.CrossPlatform/      Avalonia 畫面與平台實作
Controls/                               WPF 波形控制項
Models/                                 音檔、工作與音色模型
Services/                               FFmpeg、AI、預設、工作檔與草稿
Tests/                                  兩套回歸與共用測試
scripts/Publish-Desktop.ps1              桌面發布腳本
tools/                                  本機工具；執行檔不入 Git
PodcastBatchCleaner.sln                  Windows 完整方案
```

`bin/`、`obj/`、`.build_*/`、`release/`、處理成品與本機第三方執行檔由 `.gitignore` 排除。勿提交私人錄音、模型或含私人路徑的工作檔；推送 GitHub 不等於備份本機音檔。
