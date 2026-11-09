# 名詞對照表

## 音樂與練習

| 名詞 | 說明 | 出現位置 |
|---|---|---|
| 音分（cent） | 半音的 1/100。1 音分約為頻率差 0.06% | `NoteMath`、調音器、音準報告 |
| A4 | 基準音，預設 440 Hz，可設 415–466 Hz | `NoteMath.ValidateA4()`、設定頁 |
| MIDI 音高 | m = 69 + 12·log₂(f/A4)，C4 = 60 | `NoteMath.FrequencyToMidi()` |
| 移調樂器 | 記譜音高與實際音高不同的樂器：B♭（+2）、E♭（+9）、F（+7） | `Transposition`、`NoteMath.ToWritten()` |
| 起音（onset） | 一個音開始的時刻 | `OnsetDetector` |
| 搶拍／拖拍 | 起音比拍點早／晚；偏差負值為搶拍 | `BeatAligner`、`CsvReport.Beats()` |
| 漸快／漸慢 | 速度隨時間變快／變慢，由偏差的迴歸斜率判定 | `TempoTrend` |
| 連音（legato） | 音與音之間不斷氣，沒有音量起伏 | `Articulation.WindLegato` |
| 吐音（tonguing） | 每個音都以舌頭起音，有明顯的音量上升 | 已知限制、起音測試 |
| 餘音 | 前一個音的尾巴（撥弦衰減、房間殘響） | 起音精修、11 章 P8 |
| 顫音（vibrato） | 週期性的音高小幅擺動 | `PerformanceOptions.VibratoDepthCents` |

## 訊號處理

| 名詞 | 說明 | 出現位置 |
|---|---|---|
| FFT | 快速傅立葉轉換 | `ComplexFft`、`RealFft` |
| Bluestein（chirp-z） | 把任意長度 DFT 轉成卷積的 FFT 演算法 | `ComplexFft` 內的 `BluesteinFft` |
| DTFT | 離散時間傅立葉轉換，頻率為連續值 | `HarmonicFrequencyRefiner` |
| STFT | 短時傅立葉轉換，逐訊框做 FFT | `StftFrameAnalyzer`、`Spectrogram` |
| hop | 相鄰訊框的間隔（取樣數） | 音高軌跡 10 ms、起音 5 ms |
| MPM／NSDF | McLeod Pitch Method／正規化平方差函數 | `McLeodPitchDetector` |
| clarity | NSDF 峰值（0–1），週期性的明確程度 | `PitchEstimate.Clarity` |
| 缺少基頻 | 只有 2 次以上泛音、沒有基頻的音色 | `HarmonicSpectrum.MissingFundamental` |
| SuperFlux | 比較前先做頻率最大值濾波的頻譜通量，抑制顫音假起音 | `OnsetDetector` |
| SNR | 訊號雜訊比（dB） | 測試訊號 |
| Biquad | 二階 IIR 濾波器 | `BiquadFilter`、`BiquadDesign` |
| 雙線性轉換（預扭曲） | 把類比濾波器轉成數位濾波器，並在指定頻率對齊響應 | `KWeighting.Design()` |
| 多相濾波 | 只計算需要的輸出相位，用於過取樣與取樣率轉換 | `TruePeakMeter`、`RationalResampler` |
| Kaiser 視窗 | 可調整主瓣寬度與旁瓣衰減的視窗，用於設計 FIR | `RationalResampler`、`TruePeakMeter` |

## 響度

| 名詞 | 說明 | 出現位置 |
|---|---|---|
| LUFS | 相對於數位滿刻度的響度單位 | `LoudnessMeter` |
| LU | 響度差，1 LU = 1 dB | LRA、正規化 |
| K 加權 | BS.1770 的聽覺加權濾波（高架 + 高通） | `KWeighting` |
| 整合響度 | 整段的閘控平均響度 | `LoudnessResult.IntegratedLufs` |
| 瞬時／短期響度 | 400 ms／3 s 視窗的響度 | `LoudnessResult.Momentary`、`ShortTerm` |
| 閘控（gating） | 排除過小區塊：絕對閘 −70 LUFS、相對閘 −10 LU | `LoudnessMeter` |
| LRA | 響度範圍，短期響度的 P95 − P10 | `LoudnessResult.LoudnessRangeLu` |
| dBTP／真峰值 | 重建後類比波形的峰值（含樣本間峰值） | `TruePeakMeter` |
| 正規化 | 套用固定增益使整合響度達到目標 | `Normalizer` |

## 系統與程式

| 名詞 | 說明 | 出現位置 |
|---|---|---|
| WASAPI | Windows 音訊工作階段 API；本專案只用共用模式 | `src/PracticeMate.Audio.Windows/` |
| 交錯（interleaved） | 多聲道取樣依 L R L R… 排列 | 擷取區塊、`StreamingWavWriter` |
| 框（frame） | 一個時間點的所有聲道取樣 | `FramesProcessed`、`FrameCount` |
| SPSC | 單一生產者、單一消費者 | `SpscRingBuffer` |
| Pump | 消費者從環狀緩衝區取資料並分派的一次動作 | `CaptureSession.Pump()` |
| 消費者（consumer） | 接收擷取資料的元件：錄音檔、調音器、電平表 | `ICaptureConsumer` |
| 偽共享（false sharing） | 不同核心寫入同一 cache line 的不同變數，互相讓快取失效 | `SpscRingBuffer.Indices` |
| 組合根 | 建立並連接所有物件的唯一位置 | `App.OnStartup()` |
| 性質測試 | 以大量隨機輸入檢查「必定成立的性質」 | `Prop.ForAll()` |
| 參考實作 | 寫法不同、算的東西相同的獨立實作，用於比對 | `tests/PracticeMate.Testing.Reference/` |
| spike | 在正式實作前，針對高風險項目做的技術驗證 | 11 章 P1 |
| M0–M12 | 開發里程碑 | `docs/design/architecture.md` §11 |
| Q1–Q9 | 規格模糊點的解讀編號 | `docs/design/architecture.md` §1.4 |
| F1–F4 | 驗收時發現的問題編號 | `docs/test-report.md` §5 |
