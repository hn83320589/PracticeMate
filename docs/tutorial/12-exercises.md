# 12 延伸練習與自我檢核

## 理解檢核

1. 為什麼 `CaptureSession.OnCaptured()` 只有一行 `_ring.TryWrite(interleaved)`？如果在這裡直接寫檔會怎樣？
   <details><summary>答案</summary>
   它在 WASAPI 的即時執行緒上執行，不可阻塞。寫檔可能因磁碟忙碌而等待數十毫秒，期間 WASAPI 的緩衝會溢出，造成掉資料或爆音。所有可能慢的工作都交給消費者執行緒的 `Pump()`。見 07 章「錄音一次的完整生命週期」第一站。
   </details>

2. `Pump()` 為什麼要「先讀停止旗標，再排空」，而不是反過來？
   <details><summary>答案</summary>
   反過來的話，可能在「排空之後、讀旗標之前」裝置又送來最後一塊資料然後停止，這塊資料就會漏掉。先讀到旗標代表裝置已經停了，之後的 `Drain()` 一定拿得到所有資料。見 07 章第三站。
   </details>

3. `SpscRingBuffer` 的 `Indices` 結構為什麼把 `Write` 放在偏移 64、`Read` 放在偏移 192？
   <details><summary>答案</summary>
   讓生產者與消費者各自寫入的欄位位於不同的 cache line，避免偽共享（false sharing）。見 07 章第二站。
   </details>

4. 響度和真峰值為什麼在不同的取樣率計算？
   <details><summary>答案</summary>
   規格要求非 44.1/48/96 kHz 先轉 48 kHz 再分析，響度照做；但取樣率轉換是低通濾波，會改變樣本間峰值，所以真峰值必須用原始取樣率。程式上的差異只是 `AnalysisPipeline.Run()` 傳給 `TruePeakMeter.Measure()` 的是 `audio` 而不是 `analysisAudio`。見 01 章 Q6、07 章「分析管線」。
   </details>

5. 節拍器為什麼不能寫成每拍 `position += fs * 60.0 / bpm`？
   <details><summary>答案</summary>
   浮點累加會累積誤差，1 小時 7,200 拍後可能偏移數個取樣。改成以 k 直接整數計算 `MetronomeScheduler.BeatOffset()`，每拍誤差都 ≤ 0.5 個取樣且不累積。見 08 章第 7 節。
   </details>

6. MPM 在雜訊下有偏差，為什麼不乾脆換掉 MPM？
   <details><summary>答案</summary>
   MPM 的問題只在「量頻率」，它「選週期」的能力很好：能避免八度錯誤、能處理缺少基頻的音色，而且 NSDF 峰值可以直接當 clarity。所以保留 MPM 選週期，頻率改由 `HarmonicFrequencyRefiner` 在頻域量測。見 08 章第 2 節、11 章 P1。
   </details>

7. 為什麼 `TunerReading` 有 `EndFrame` 欄位？
   <details><summary>答案</summary>
   讓延遲可以用取樣索引精確量測（音高改變的取樣 → 讀數改變的取樣），不依賴牆上時鐘，測試也不受區塊大小影響。見 07 章第四站與 `TunerEngineTests.Block_size_does_not_change_reading_times`。
   </details>

8. 起音偵測的原始測試全部通過，為什麼還是漏了「提前 40 ms」的缺陷？
   <details><summary>答案</summary>
   測試合成器所有音的音量相同，新音一定遠大於前音餘音，從未產生觸發缺陷的情境。見 11 章 P8。
   </details>

## 動手練習

### 練習 1：新增一個移調樂器（例如 A 調單簧管，記譜比實際高 3 個半音）

需要改動的地方：

- `src/PracticeMate.Core/Music/NoteMath.cs`：`Transposition` 列舉與 `TranspositionSemitones()`。
- `src/PracticeMate.App/Converters.cs`：`TranspositionNameConverter` 的顯示名稱。
- 設定頁的選項來源（從 `SettingsViewModel` 找）。
- `tests/PracticeMate.Core.Tests/Music/NoteMathTests.cs` 加測試。

提示：`AppSettings` 以 JSON 儲存列舉，新增成員不影響既有設定檔的讀取。

### 練習 2：在節拍報告加入「最大搶拍」與「最大拖拍」

提示：`BeatReport` 是 record（`src/PracticeMate.Core/Analysis/Beats/BeatAligner.cs`），在 `Align()` 裡計算後加入新屬性；`CsvReport.Beats()` 的結尾統計列也要加；先在 `tests/PracticeMate.Core.Tests/Analysis/BeatAlignerTests.cs` 寫測試（用 `PerformanceSynth.RhythmExercise()` 產生已知偏差的序列）。

### 練習 3：解決起音的 0.2% 極端情況

這是真實的未解問題（11 章 P8）。一個方向：把精修用的包絡從「全頻帶 RMS」改成「新音的泛音頻帶能量」。新音與餘音音高不同時，在新音的頻帶上餘音的能量很小，上升會清楚得多。

提示：
- 先把 `OnsetDetectorTests` 中的隨機情境（不截斷餘音＋0–0.5 s 殘響＋−10–0 dB 隨機音量）寫成性質測試，確認它目前會失敗。
- 同音連續吐音（新音與餘音同音高）時這個方法無效，需要保留目前的做法作為後備。
- 注意效能：起音偵測目前約 0.5 秒（10 分鐘音訊）。

### 練習 4：讓分析支援超過 2 GB 的音訊而不配置巨大陣列

提示：設計文件 R7 原本規劃用分段陣列。從 `src/PracticeMate.Core/Audio/AudioData.cs` 開始，評估哪些演算法需要隨機存取整段訊號（音高軌跡、頻譜圖都只需要一個視窗），哪些需要整段（`MinMaxIndex`）。

## 延伸學習

| 主題 | 本專案位置 | 搜尋關鍵字 |
|---|---|---|
| 無鎖單生產者單消費者佇列 | `SpscRingBuffer` | `SPSC lock-free ring buffer`、`false sharing cache line`、`acquire release semantics .NET Volatile` |
| 任意長度 FFT | `ComplexFft` | `Bluestein chirp-z FFT`、`real FFT packing N/2` |
| 音高偵測 | `McLeodPitchDetector` | `McLeod Pitch Method NSDF`、`YIN pitch detection`、`DTFT peak maximum likelihood frequency estimation` |
| 起音偵測 | `OnsetDetector` | `SuperFlux onset detection Böck`、`spectral flux onset`、`MIREX onset evaluation` |
| 響度量測 | `LoudnessMeter`、`KWeighting` | `ITU-R BS.1770-4`、`EBU Tech 3342 loudness range`、`bilinear transform prewarping` |
| 真峰值 | `TruePeakMeter` | `inter-sample peak true peak oversampling`、`polyphase FIR Kaiser window` |
| 統計檢定 | `StudentT` | `regularized incomplete beta continued fraction Lentz`、`Lanczos approximation log gamma` |
| 可測試的時間 | 全專案的 `TimeProvider` | `.NET TimeProvider FakeTimeProvider` |
| 架構規則測試 | `tests/PracticeMate.Architecture.Tests` | `System.Reflection.Metadata PEReader`、`architecture tests ArchUnit NetArchTest` |
| 性質測試 | `Prop.ForAll` | `property-based testing shrinking`、`FsCheck` |
