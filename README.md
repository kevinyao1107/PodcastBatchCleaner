# 音檔批次調整工具

這是一個以 .NET 8 製作的音檔批次處理工具，主要用來批次移除靜音片段、降噪、減少空間音，並可在處理前後播放比對音檔。

專案包含兩個版本：

- `PodcastBatchCleanerWpf.csproj`：Windows WPF 版本
- `PodcastBatchCleaner.CrossPlatform/PodcastBatchCleaner.CrossPlatform.csproj`：Avalonia 跨平台版本，目標為 Windows、macOS、Linux

也可以直接開啟 `PodcastBatchCleaner.sln` 檢視完整方案。

## 與 SoundLab 的功能對齊與版本範圍

本工具可與 [SoundLab 網頁版](https://github.com/kevinyao1107/soundlab-audio-studio) 交換 `audio-studio-presets` JSON v2 預設，並相容 v1 匯入。已對齊的設定包含音量增益、LUFS 目標、靜音剪除門檻／秒數／保留時間、共用聲道模式、三頻 EQ 與齒音頻帶削減。

Windows WPF 版已提供每檔剪輯、30 秒試聽、原音波形定位、品質分析、整集合併、片頭／片尾、資訊／封面及成品峰值驗證。SoundLab 也已具備相應的網頁工作流程，包括整集成品預聽、依處理後長度定位段落與重新下載；格式、AI 能力、儲存方式及不同 DSP 的音色／靜音剪接點仍有差異。

本 README 的新增介面功能以 Windows WPF 版為準。Avalonia 跨平台專案共用部分核心服務，但尚未全面提供 WPF 的共用預設、音色控制與波形介面；能建置不代表兩個介面功能完全一致。

### 驗證狀態與後續優先順序

本次修改已通過 `Tests/RegressionChecks.csproj` 的完整回歸檢查（包含實際 FFmpeg、DeepFilterNet、預設對齊、EQ／齒音削減、波形、取消及輸出峰值驗證），WPF 與 Avalonia 建置均為零警告、零錯誤。這些自動化檢查不代替所有音效的真實錄音聽感驗收。

下一步優先以真實 Podcast 錄音驗收音色、剪接點、30～60 分鐘長音檔、取消與輸出速度，再視使用需求補齊 Avalonia。瀏覽器版的大型壓縮來源解碼與中間檔磁碟暫存屬 SoundLab 後續工作，不影響桌面版既有原生 FFmpeg 與磁碟輸出流程。

## 功能

- 批次載入資料夾內的音檔
- 支援處理單一選取檔案或全部檔案
- 可為每個音檔自訂輸出檔名
- 移除超過指定秒數的靜音片段
- 可調整靜音判定門檻 dBFS
- 內建常用處理模板，可一鍵套用後製參數
- 統一降噪
- 降噪強度可選輕度、標準、強力；AI 成功處理後略過重複的 FFmpeg 降噪
- 減少空間音，降低房間感與低中頻箱音
- 空間感抑制，以 EQ、降噪與壓縮改善空曠感，並可調整處理強度
- 人聲增厚 EQ，補足偏薄的人聲並讓聲音更靠前
- 獨立三頻 EQ，可調整低、中、高頻增益
- 齒音抑制，可調整高頻削減強度（頻帶 EQ，非動態 De-esser）
- 自訂預設的命名、保存、套用、刪除，以及共用 JSON 預設匯入匯出
- 聲道處理，可保持原聲道、置中、轉 Mono、指定左/右聲道或調整左右平衡
- Podcast 響度標準化，讓分段錄音音量更一致
- 防爆音 Limiter，降低突然大聲造成的爆音風險
- 音量增益調整
- 播放原始音檔與處理後音檔
- 可快速開啟輸出資料夾、原始檔案位置與處理後檔案位置
- 開始處理前會檢查 FFmpeg、輸出資料夾、檔案路徑與剪輯時間
- 處理時顯示進度視窗、目前檔名與百分比；AI 模型處理階段會以處理中狀態顯示
- 處理完成後顯示摘要，包含成功、失敗、取消數量與輸出位置
- 可中途取消處理
- 自動保存常用設定，下次開啟時載入
- 自動將輸出檔存到指定資料夾
- 可指定 AI 處理後音檔資料夾，批次配對 AI 音檔後再進行後製
- 可指定 DeepFilterNet，直接用本機 AI 模型先做語音降噪
- 可指定 VoiceFixer，使用免費開源的人聲修復模型輔助改善殘響、失真與薄聲
- DeepFilterNet 失敗時可自動改用 FFmpeg 繼續處理
- 可輸出 A/B 比較版本，快速比較不同處理模板效果
- A/B 可使用兩階段響度匹配到 -16 LUFS，避免較大的音量影響比較
- 可輸出 30 秒試聽版本，快速確認目前參數效果
- 右側以頁籤分類快速處理、AI / 修復、剪輯統整、播放分析功能
- 可分析音檔品質，查看長度、平均音量、Peak、LUFS 與 True Peak，分析中可取消
- 可為每個音檔設定剪輯開始與結束時間
- WPF 可讀取原音波形、點擊定位，並以游標設定剪輯起點與終點
- 可將多段錄音依清單順序統整成單一音檔
- 可用上移、下移或拖曳調整統整輸出順序
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

Avalonia 跨平台版本會依平台尋找 `ffmpeg` / `ffmpeg.exe`，播放功能另需要 `ffplay` / `ffplay.exe`。目前 Windows WPF 版本是主力版本，功能最完整；Avalonia 跨平台版本仍屬精簡版。

## 安裝 FFmpeg

Windows 可下載 FFmpeg 後，將 `ffmpeg.exe` 放到：

- 專案根目錄的 `tools/FFmpeg/`（建議，同時放入 `ffprobe.exe` 與 `ffplay.exe`）
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
dotnet build .\PodcastBatchCleaner.sln -c Release
```

只編譯 Windows WPF 版本：

```powershell
dotnet build .\PodcastBatchCleanerWpf.csproj -c Release
```

完成後，Windows 執行檔位於專案根目錄下：

```text
bin/Release/net8.0-windows/PodcastBatchCleanerWpf.exe
```

此為編譯輸出，不是獨立安裝包；執行時保留同資料夾的 DLL、設定等檔案，並安裝 .NET 8 Desktop Runtime。從原始碼編譯則需要 .NET 8 SDK。FFmpeg 與 AI 工具需另行配置，不會因編譯而自動下載。

只編譯 Avalonia 跨平台版本：

```powershell
dotnet build .\PodcastBatchCleaner.CrossPlatform\PodcastBatchCleaner.CrossPlatform.csproj -c Release
```

## 測試

Windows 回歸檢查（將 FFmpeg 路徑替換成實際位置）：

```powershell
dotnet run --project .\Tests\RegressionChecks.csproj -- "C:\tools\ffmpeg\bin\ffmpeg.exe"
```

檢查剪輯時間驗證、限幅器輸出峰值、AI 聲道選取、立體聲還原、取消子程序與半成品清理。
另檢查動態 EQ、原音混合的時間對齊與音量匹配、AI 檔案精確配對與歧義拒絕、不等長立體聲的尾段保留、工具搜尋，以及 VoiceFixer 腳本的空白／中文／特殊字元路徑。可在 FFmpeg 路徑後加上 DeepFilterNet 路徑，實測本機 AI 工具參數。
也檢查 A/B 原音與目前設定、實際音源的剪輯時長、FFmpeg 時長備援、片尾淡出、整集響度／限幅、全靜音輸出、M4A 封面與標籤，以及響度分析的取消與既有輸出保護。
另測編碼後真峰值、預設 48 kHz、驗證階段進度、超標重試上限與驗證失敗／取消的舊檔保護。AAC／MP3 測試會先檢查 FFmpeg 是否包含相應編碼器；缺少時會明確列為 SKIP，不算測試通過。
WPF UI 檢查會在隔離設定的 STA 執行緒建立視窗但不顯示，驗證控制項繫結、AI 選項連動、0% 修復的模型略過、警告保留與處理階段紀錄，不會讀寫使用者設定。可在 DeepFilterNet 路徑後再加上畫面輸出資料夾，產生 1180 x 760 與 980 x 640 的離屏 UI 檢查圖。
共用預設與音色設定的檢查包含 JSON 格式／版本驗證、參數範圍與三頻 EQ／齒音設定。

長錄音壓力測試（需至少 30 分鐘的錄音及本機 DeepFilterNet）：

```powershell
dotnet run --project .\Tests\RegressionChecks.csproj -- --long-audio "D:\recording.m4a" "C:\tools\ffmpeg\bin\ffmpeg.exe" "C:\tools\DeepFilterNet\deep-filter.exe" ".build_long_audio_check"
```

此模式會測試完整 FFmpeg／DeepFilterNet 流程、途中取消、進度更新、WPF Dispatcher 回應時間及暫存清理。輸出會放在指定資料夾下的新測試子目錄，產生 `report.json` 與兩份處理結果；大型 AI 暫存 WAV 會在結束時清除。測試會比對原檔 SHA-256，且不覆蓋原始錄音或讀寫使用者設定。這是隔離的處理服務與 Dispatcher 測試，不會操作使用者正在開啟的軟體視窗，也不代表已驗證錄音的聽感品質。

完整錄音 MP3 驗證可使用 `--mp3-audio` 模式，三個參數依序是原音路徑、FFmpeg 路徑與測試輸出資料夾。測試會另外輸出 MP3，檢查 48 kHz、成品真峰值、原檔 SHA-256 與暫存清理。

## 本機 FFmpeg 工具

Windows 專案可使用以下工具配置：

```text
tools/FFmpeg/ffmpeg.exe
tools/FFmpeg/ffprobe.exe
tools/FFmpeg/ffplay.exe
```

目前本機使用 Gyan FFmpeg 9.0.2 essentials 建置，包含 `libmp3lame`、AAC、PCM 與專案所需的內建音訊濾鏡。來源是 [FFmpeg 官網列出的 Windows 建置供應方](https://ffmpeg.org/download.html)：[Gyan 建置頁](https://www.gyan.dev/ffmpeg/builds/)。版本、下載位址與核對過的 SHA-256 記錄在 `tools/FFmpeg/README.md`，授權文件保留在同一資料夾。

自動搜尋會優先使用專案內的工具，也能從 `bin/Release/net8.0-windows` 找到專案根目錄的工具。已手動指定且仍有效的 FFmpeg 路徑會保留；WPF 在批次、試聽及 A/B 開始時先檢查所選輸出編碼器。若原工具不支援選定格式，但專案工具支援，會明確記錄警告並改用專案工具，再進行 AI 與音訊處理。

執行檔是本機第三方依賴，不提交 Git。換電腦或重新取得專案後，需要下載工具並放到上述位置；不會修改系統 PATH，也不會改動剪映或其他軟體的 FFmpeg。

## 使用方式

1. 點選「選取資料夾」，載入要處理的音檔。
2. 視需要點選「輸出位置」指定輸出資料夾。
3. 如果程式沒有自動找到 FFmpeg，點選「指定 FFmpeg」選取 `ffmpeg.exe`。
4. 如果已有 AI 工具處理後的音檔，在「AI / 修復」頁籤點選「AI 音檔資料夾」並勾選「使用 AI 處理後音檔」。
5. 如果要用本機 AI 降噪，可勾選「DeepFilterNet AI 降噪」並指定 `deep-filter.exe`。
6. 如果要用免費人聲修復模型輔助去殘響，可勾選「VoiceFixer 人聲修復」並指定 `voicefixer.exe` / `voicefixer.cmd`。
7. 可先套用「一般 Podcast」「空間音重」「聲音偏薄」「只剪輯」模板，再微調靜音秒數、靜音門檻、降噪、減少空間音、空間感抑制強度、聲道模式、人聲增厚 EQ、響度標準化、防爆音、音量增益與輸出格式。
8. 右側控制面板分為「快速處理」「AI / 修復」「剪輯統整」「播放分析」頁籤。
9. 如需快速確認參數效果，在「快速處理」頁籤選取單一音檔後點選「30秒試聽」。
10. 如需比較不同處理效果，在「快速處理」頁籤選取單一音檔後點選「A/B 比較」。
11. 如需剪輯，可在清單中的「開始」「結束」欄位輸入時間。
12. 如需改檔名，可在清單中的「輸出檔名」欄位輸入新檔名。
13. 如需合併多段錄音，可用「上移」「下移」或拖曳清單列調整順序，並勾選「統整成單一檔案」。
14. 統整輸出時可視需要填寫標題、作者、節目名稱，並選擇封面圖片。
15. 可視需要選擇片頭、片尾，並調整淡入淡出秒數。
16. 如需檢查音檔品質，可在「播放分析」頁籤點選「分析原始」或「分析處理後」，分析中可按「取消分析」。
17. 點選「處理選取」或「全部輸出」。
18. 開始處理前，程式會先檢查 FFmpeg、DeepFilterNet、VoiceFixer、輸出資料夾、檔案路徑、剪輯時間與片頭片尾/封面路徑。
19. 處理時可在進度視窗查看目前檔案、處理階段與百分比，也可按「取消」中止。DeepFilterNet 與 VoiceFixer 執行時因工具本身不提供百分比，進度視窗會顯示 AI 處理中。
20. 處理結束後會顯示摘要；一般批次中單一檔案失敗時，程式會繼續處理後面的檔案。
21. 可使用「開啟輸出」「開原檔位置」「開處理後檔」快速查看檔案。

剪輯時間可輸入秒數或時間格式，例如：

```text
12.5
01:23
1:02:03
```

「開始」留空代表從音檔開頭處理，「結束」留空代表處理到音檔結尾。

## AI 前處理音檔

你可以先用 Adobe Podcast Enhance、Auphonic、DaVinci Resolve Voice Isolation 或其他 AI 工具處理音檔，再把輸出檔放到 AI 音檔資料夾。

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

檔名只接受上述完整名稱（不區分大小寫），不會用相似前綴猜測，例如 `episode1` 不會配到 `episode10_ai`。同一原檔若找到多個對應 AI 檔，程式會停止並列出候選名稱，請在 AI 音檔資料夾只保留一個對應版本後重試。

## DeepFilterNet AI 降噪

程式可指定本機安裝的 DeepFilterNet `deep-filter.exe`，先做 AI 語音降噪，再接續原本的 FFmpeg 後製流程。

DeepFilterNet 不會包在此專案內，需要另外安裝或下載。設定方式：

1. 勾選「DeepFilterNet AI 降噪」。
2. 點選「指定 DFN」，選取 `deep-filter.exe`。
3. 視需要保留「DeepFilterNet PostFilter」，通常可以讓殘留噪音更少。
4. 點選「處理選取」「全部輸出」或「A/B 比較」。

如果把 `deep-filter.exe` 放在以下位置，程式啟動時會自動找到：

```text
tools/DeepFilterNet/deep-filter.exe
```

第一次使用或設定檔還沒有 DeepFilterNet 偏好時，程式找到這個路徑後會自動把 DeepFilterNet 設為預設 AI 工具。之後若手動取消勾選，程式會記住你的選擇。

工具搜尋也會檢查執行檔所在資料夾的上層 `tools` 位置，因此直接執行 `bin/Release/net8.0-windows` 中的程式也能找到專案根目錄的工具；VoiceFixer 同樣適用。

啟用後，程式會先依剪輯範圍把音檔暫時轉成 48 kHz mono WAV，交給 DeepFilterNet 處理，再套用靜音移除、EQ、響度標準化、Limiter 與輸出格式設定。「左／右聲道為主」會先選取原始聲道；「保持原聲道」或「左右平衡調整」在立體聲音檔上會分別處理左右聲道再合回，因此 AI 處理時間會增加。暫存檔會在處理結束後自動刪除。

DeepFilterNet 比較適合處理背景噪音、風扇聲、環境底噪。若主要問題是房間反射或錄音太遠，它可能只能改善一部分，仍建議搭配「減少空間音」與「人聲增厚 EQ」微調。

「降噪強度」的輕度／標準／強力在 DeepFilterNet 分別使用 10／20／100 dB 降噪上限，並啟用模型延遲補償；FFmpeg 備援則使用 6／12／24 dB 降噪量。預設標準。DeepFilterNet 或 VoiceFixer 成功後會略過 FFmpeg 的額外降噪，避免重複處理。PostFilter 可另行關閉，以比較較自然的人聲。

「減少空間音」使用 65 Hz 高通與低中頻動態 EQ；「空間感抑制」（舊稱「去殘響」）再加入動態 EQ 和柔和擴展器，減少停頓時的底噪與拖尾，避免持續削減人聲低中頻。這些是聽感調整，不是專用的殘響消除演算法。相關處理需要 FFmpeg 支援 `adynamicequalizer` 和 `agate`。建議先用「30秒試聽」比較；需要模型修復時可另啟用 VoiceFixer。

防爆音在音量增益之後執行，關閉限幅器自動音量補償。PCM 輸出使用約 -1.5 dBFS 的取樣峰值限幅；AAC／MP3 等有損輸出先保留約 -3 dBFS 餘裕，但不單靠這個值保證成品峰值。

啟用防爆音或響度標準化時，單檔、試聽、A/B 響度匹配與統整輸出會在正式發布前解碼暫存成品，驗證真峰值不超過 -1.5 dBTP，並保留量測四捨五入的餘裕。若超標，最多三次從原始輸入及相同處理參數重新輸出，再降低最終增益；不會反覆壓縮已編碼的成品。無法驗證、仍超標或取消時不發布新檔，保留舊輸出並移除半成品。修正峰值可能讓整體響度略低於 -16 LUFS。

進度視窗會顯示「驗證輸出真峰值」或「修正編碼峰值」，驗證完成才到 100%。完整成品驗證會增加一次解碼分析，長音檔需要額外處理時間。若防爆音與響度標準化都關閉，一般輸出不會強制限制峰值。

處理、試聽、A/B 比較與品質分析一次只執行一項工作。工作期間會停用衝突的設定；關閉進度視窗會要求取消，關閉主視窗則會等待工作停止並清理後離開。AI 備援原因可在「播放分析」查看，批次摘要也會列出警告。

未啟用 DeepFilterNet 時會停用 PostFilter；未啟用 VoiceFixer 時會停用其模式與混合比例。混合比例為 0% 時略過 VoiceFixer 模型，因此不需要安裝模型，模式與 AI 備援選項也會依實際啟用的工具連動。降噪強度只在 FFmpeg 降噪或 DeepFilterNet 啟用時可調整。停用控制項不會清掉原先設定。

主畫面上方會顯示最近音檔已完成的處理階段，成功的 AI 工具才會列入。AI 執行失敗、外部 AI 檔未找到或原音混合退回純修復結果時，上方會出現可展開的警告區，完成後仍會保留到下一次處理。警告明細最多列出 20 則，總數仍會持續計算；音檔清單的狀態提示也會保留該檔的已完成階段與最近警告。

## VoiceFixer 免費人聲修復

VoiceFixer 是免費開源的人聲修復工具，可用來嘗試改善殘響、失真、低解析與部分噪音。它不是單純 EQ，而是會重新修復人聲，因此效果可能更明顯，但也可能改變音色，建議先用「30秒試聽」或「A/B 比較」確認。

「人聲修復混合比例」預設 60%，0% 使用修復前音檔並略過模型，100% 使用純修復結果。中間比例會以短片段估計時間偏移與音量差，再對齊混合；長音檔會抽查第二段是否有偏移漂移。片段太短、波形相似度不足或偏移不一致時，會使用純修復結果並在警告中說明，不會硬混合。修復前音檔指 VoiceFixer 的輸入，可能已經經過 DeepFilterNet。

安裝方式可參考 VoiceFixer 官方專案：

```text
pip install git+https://github.com/haoheliu/voicefixer.git
```

如果把 `voicefixer.exe`、`voicefixer.cmd` 或 `voicefixer.bat` 放在以下位置，程式啟動時會自動找到：

```text
tools/VoiceFixer/
```

VoiceFixer 模式：

- `0`：標準模型，官方建議的預設模式。
- `1`：加入高頻移除的預處理。
- `2`：訓練模式，可試用於嚴重劣化的語音，不代表修復強度等級。

目前沒有把 NARA-WPE 直接內建到主流程，因為它偏研究型 Python 套件，單聲道 podcast 效果與部署穩定性需要另外測試。後續可再新增「自訂外部去殘響命令」來支援 NARA-WPE 或其他工具。

## 聲道處理

在「AI / 修復」頁籤可調整聲道模式：

- 左右混合置中：適合大多數 Podcast 人聲，讓聲音穩定在中央。
- 轉單聲道 Mono：適合只需要口語內容、想避免左右聲道不一致的輸出。
- 保持原聲道：不改變原本聲道。
- 左聲道為主 / 右聲道為主：適合錄音時只有其中一邊比較乾淨。
- 左右平衡調整：保留 stereo，但可把偏左或偏右的音量拉回來。

如果分段錄音最後要統整，通常建議使用「左右混合置中」。若音檔本身是音樂或需要保留空間感，改用「保持原聲道」會比較合適。

AI 分別處理左右聲道後，合成時會保留較長聲道的完整尾段，較短聲道補靜音，不會因長度不同而截掉另一側的內容。這不代表能自動修正模型產生的左右時間偏移。

若勾選「AI 失敗改用 FFmpeg」，DeepFilterNet 發生錯誤時，該檔案會回到原始音檔或已配對的 AI 音檔，繼續套用 FFmpeg 後製。若取消勾選，DeepFilterNet 錯誤會讓該檔案直接失敗，方便排查 AI 工具問題。

## 30 秒試聽

選取單一音檔後點選「30秒試聽」，程式會用目前參數處理一段 30 秒音檔，輸出到：

```text
processed/preview/
```

如果有填「開始」「結束」剪輯時間，試聽會從設定的開始時間往後取 30 秒，且不超過結束時間。完成後程式會自動切到處理後音檔並播放，方便快速比較調整效果。

## 音檔品質分析

在「播放分析」頁籤可分析選取音檔的原始版本或處理後版本。分析結果包含：

- 長度
- 平均音量
- Peak
- LUFS
- True Peak

Peak 接近 0 dB 時，程式會標示可能接近爆音。Podcast 輸出通常可用 LUFS 觀察是否接近 -16 LUFS。

## A/B 比較

選取單一音檔後點選「A/B 比較」，程式會將同一段音檔輸出成多個模板版本，方便直接用耳朵比較處理效果。

目前會輸出：

- 原音（只剪輯）
- 目前設定
- 一般 Podcast
- 空間音重
- 空間感抑制
- 聲音偏薄

輸出檔會放在輸出資料夾中的 `ab_compare/`。

「原音（只剪輯）」永遠取原始音檔，不套用 AI、降噪、EQ、聲道轉換或空白移除，只套用指定剪輯範圍。「目前設定」使用目前選取的參數與 AI 流程。勾選「A/B 等音量比較」時，所有版本（包含原音）會另外匹配到 -16 LUFS，避免音量大小影響判斷；關閉後原音不做響度調整。比較完成後，清單的處理後檔案會指向「目前設定」版本。

比較檔仍依選定的輸出格式編碼，預設使用 48 kHz；「原音」表示未套用聲音補強，不保證與來源檔案位元相同。

外部 AI 檔的長度可能與原始音檔不同，剪輯驗證及試聽範圍會依實際處理音源判定，不會把 AI 檔長度寫回原始音檔的長度欄位。A/B 的剪輯範圍也需要在原音中有效。

## 統整輸出

統整輸出模式會依照清單順序處理多段錄音，先把每段套用目前的後製設定，再合併成單一音檔。

可設定：

- 段落間空白秒數
- 輸出格式
- 統整輸出檔名
- 每段音檔的剪輯開始與結束時間
- 上移、下移或拖曳清單列調整段落順序
- Podcast 標題、作者 / 主持人、節目 / 專輯名稱
- 封面圖片
- 片頭音檔與片頭淡入秒數
- 片尾音檔與片尾淡出秒數
- 是否使用 AI 處理後音檔
- 降噪、減少空間音、空間感抑制、人聲增厚 EQ、響度標準化與 Limiter

統整輸出順序為：片頭、清單中的分段音檔、片尾。片頭與片尾會先轉成合併用暫存音檔，再與分段錄音一起輸出成單一檔案。

勾選「響度標準化」時，最後會再分析包含片頭、片尾及段落間空白的整集音訊，以兩階段方式朝 -16 LUFS／-1.5 dBTP 目標處理；勾選 Limiter 時，整集輸出最後也會套用限幅，包括片頭片尾。這不是自動調整音樂與人聲的相對音量，素材本身差距太大仍需先調整。分析期間進度視窗會顯示「整集響度分析」，可取消。

時長會優先由 ffprobe 取得；沒有 ffprobe 或其結果無效時，改讀取 FFmpeg 的音檔資訊。Linux/macOS 會搜尋 `ffprobe`，Windows 搜尋 `ffprobe.exe`。片尾淡出需要有效時長，若仍無法判定，程式會明確報錯而不會默默略過淡出。

封面圖片會在最後合併輸出時嵌入音檔。建議使用正方形 JPG 或 PNG，例如 1400 x 1400 或 3000 x 3000。WAV 格式主要用於無壓縮音訊，封面相容性較差，程式會略過封面嵌入。

如果沒有輸入副檔名，程式會依目前選擇的輸出格式自動補上副檔名。

正式單檔、試聽、A/B 與統整輸出預設均使用 48 kHz，避免響度濾鏡的內部取樣率導致成品意外升到 96 kHz。AI 前處理與合併用 PCM 暫存檔可使用各自需要的取樣率；程式內部明確指定取樣率時會保留指定值。

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
│   └── AudioWaveform.cs
├── Controls/
│   └── AudioWaveformView.cs
├── Services/
│   ├── FfmpegAudioProcessor.cs
│   ├── DeepFilterNetAudioProcessor.cs
│   ├── VoiceFixerAudioProcessor.cs
│   ├── AudioBlendProcessor.cs
│   ├── BundledAudioToolLocator.cs
│   └── PortablePresetFile.cs
├── Tests/
│   ├── RegressionChecks.csproj
│   └── fixtures/
├── tools/
│   ├── FFmpeg/
│   ├── DeepFilterNet/
│   └── VoiceFixer/
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
- Windows WPF 版本會在關閉程式時保存常用設定到使用者 AppData，下次開啟時會自動還原設定並載入上次資料夾。
- `processed/`、`bin/`、`obj/`、`.build_*` 等輸出資料夾已在 `.gitignore` 中排除。

## 共用預設

桌面版使用 `audio-studio-presets` JSON 與 SoundLab 交換多組預設。目前匯出 `schemaVersion 2`，匯入相容第一版與第二版。核心欄位包含音量增益（−12～12 dB）、響度開關與 −14／−16／−18／−23 LUFS、靜音剪除開關、門檻（−70～−25 dBFS）、最短空白（0.1～10 秒）、前後保留（0～0.5 秒）及聲道（保留、Mono、左／右 Mono、左右互換）。音量增益在響度正規化之前套用；啟用正規化時，最後響度以目標及峰值保護為準。

預設最多 100 組、檔案上限 1 MB；匯入先檢查版本與全部核心參數。SoundLab 仍接受舊的 `soundlab-presets` JSON。桌面版在「快速處理」提供命名、保存、套用、刪除及 JSON 匯入匯出，並保存到本機設定。

`extensions.soundlab`／`extensions.desktop` 保存各自的非 AI 效果。跨工具套用時，只交換可對應的核心設定，停用無法等效對應的額外效果／AI 並顯示提示；不同 DSP 不承諾相同音色。AI 工具、資料夾路徑、整集資訊與剪輯範圍不包含在共用預設內。桌面版舊的「左右混合置中」、左／右複製成 stereo 與手動平衡模式不屬於這版共用聲道集合，保存共用預設前需切換至支援模式；超出共用增益或門檻範圍也會要求調整，不會暗中改值。

兩邊靜音判定引擎仍不同；相同門檻、秒數與保留時間不保證完全相同的剪接點。桌面版 A/B 等音量比較固定使用 −16 LUFS，與正式輸出目標分開；SoundLab 則以處理後的響度增益近似匹配原音與成品的聽感，不是固定 −16 LUFS 的另一次母帶處理。

### 三頻 EQ 與齒音抑制

第二版在 `core.tone` 包含三頻 EQ 開關、低／中／高頻增益（−12～12 dB）、齒音抑制開關與強度（0～100%），必須提供完整音色設定。人聲增厚／清晰度 EQ、壓縮、降噪、去殘響與 AI 仍是各工具專用效果，不會用相同百分比猜測轉換。

三頻 EQ 使用 140 Hz 低架、1.1 kHz 中頻峰形與 8.5 kHz 高架；齒音抑制與 SoundLab 現有方法一致，使用 7.2 kHz 頻帶 EQ，100% 對應約 −9 dB。這是音色調整，不是動態語音偵測的 De-esser。桌面版新增獨立開關與調整值，套用到單檔、試聽、批次、目前設定 A/B 與整集錄音段落，並保存至本機及共用預設。啟用時使用 48 kHz 處理濾鏡，避免低取樣率來源的高頻濾鏡超過 Nyquist 頻率；最後仍依輸出取樣率設定編碼。

SoundLab 的「人聲 EQ」與「三頻段 EQ」改為獨立開關；舊的本機／JSON 預設匯入時，三頻開關沿用舊 EQ 開關，保留原有效果。


## 原音波形與剪輯定位（WPF）

在「播放分析」選取「讀取原音波形」，掃描完整原始音檔；點擊波形定位原音，方向鍵可移動一秒。金線內是剪輯保留範圍，兩側暗色區域會被剪掉；以「游標設為起點／終點」填入該檔案的剪輯時間，也可直接編輯原有時間欄位。無效範圍會顯示原因。

波形使用 48 kHz 解碼串流、最多 1024 個峰值區段及固定讀取緩衝區；各聲道分別取絕對峰值，避免反相立體聲相加後消失。它是定位用的視覺摘要，精確音量及 True Peak 仍請使用品質分析。讀取可取消，切換檔案後不會套用前一檔案的波形。處理後播放不會把不同時間軸的播放位置覆蓋到原音波形；再次點擊波形會切換回原音。Avalonia 介面尚未提供此控制項。

波形回歸測試涵蓋長串流末端瞬間峰值、反相立體聲、靜音尾段、MP3 解碼長度、無效音檔、PCM 解碼途中取消、剪輯欄位同步，以及原音／處理後時間軸切換。
