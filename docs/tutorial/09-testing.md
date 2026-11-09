# 09 測試

訊號處理系統的測試有一個根本難題：**你怎麼知道「正確答案」是什麼？** 真實錄音沒有標準答案（誰知道這個音實際偏了幾音分？），而用正式程式碼算出的結果當答案，等於自己驗證自己。

這個專案的答案是三個策略：合成已知真值的訊號、寫獨立的參考實作、用性質測試涵蓋大量隨機情境。這一章介紹這三個策略與測試的組織方式。

## 測試專案

| 專案 | 測試數 | 測什麼 |
|---|---|---|
| `tests/PracticeMate.Core.Tests` | 436 | 所有演算法、WAV、環狀緩衝區、擷取工作階段、節拍器、範例錄音 |
| `tests/PracticeMate.App.ViewModels.Tests` | 65 | ViewModel 行為：調音器顯示、節拍器設定、錄音狀態機、時間軸縮放、報告匯出 |
| `tests/PracticeMate.Architecture.Tests` | 10 | 組件之間的相依規則、NAudio 型別白名單 |
| `tests/PracticeMate.App.Tests` | 2 | WPF 實際繪製（PNG 匯出），需要 STA 執行緒 |
| `tests/PracticeMate.Performance.Tests` | 2 | 10 分鐘完整分析時間、調音器單區塊時間（`Category=Performance`） |
| **合計** | **515**（CI 執行 513，效能測試另跑） | |

輔助專案（不含測試，只提供工具）：

| 專案 | 內容 | 可以引用 Core？ |
|---|---|---|
| `tests/PracticeMate.Testing.Reference` | 參考實作、訊號產生器、性質測試工具 | **不可以**（架構測試強制） |
| `tests/PracticeMate.Testing.Fakes` | `FakeCaptureDevice`、`FakeRenderDevice`、`FakeAudioSystem` | 可以（必須實作 Core 的介面） |

把參考實作與假裝置分成兩個專案，是為了讓「參考實作不引用 Core」可以被機器檢查。如果放在同一個專案，假裝置引用 Core，這條規則就無從驗證。

> 📄 依據：README「參考實作」「假裝置放在另一個專案 `Testing.Fakes`，因為它們必須實作 Core 的介面」

## 執行指令

```powershell
dotnet test PracticeMate.sln -c Release --filter "Category!=Performance"   # CI 使用的命令
dotnet test PracticeMate.sln -c Release                                    # 全部（含效能測試）
dotnet test tests/PracticeMate.Core.Tests -c Release --filter "FullyQualifiedName~OnsetDetectorTests"   # 只跑一個類別
$env:PM_PROP_SEED = "57070651"; dotnet test tests/PracticeMate.Core.Tests -c Release   # 重跑某個性質測試的失敗種子
```

## 策略一：合成已知真值的訊號

`tests/PracticeMate.Testing.Reference/SignalGenerator.cs` 與 `tests/PracticeMate.Testing.Reference/PerformanceSynth.cs` 產生「答案已知」的音訊：

- `SignalGenerator`：純音、鋸齒波、方波、缺基頻（2–6 次泛音）、白雜訊（指定 SNR）。
- `PerformanceSynth`：依音符序列合成一段演奏，回傳訊號＋真值（每個音的起音取樣、實際 MIDI 音高）。支援管樂（30 ms 起音）、連音（滑音）、撥弦、打擊、顫音、刻意走音、刻意搶拍拖拍、漸快、每音音量、不截斷餘音、房間殘響。

最後三項（`Gain`、`RingOut`、`ReverbSeconds`）是驗收後才加的。原本的合成器所有音量相同，從未產生「新音不比前音餘音大」的情況，因此沒有測出起音被提前 40 ms 的缺陷。這是一個重要的教訓：**合成測試只能測到合成器能產生的情境**（見 11 章 F4）。

## 策略二：獨立的參考實作

| 參考實作 | 對照的正式實作 | 為什麼可信 |
|---|---|---|
| `ReferenceDft`：O(N²) 直接 DFT | `ComplexFft`、`RealFft` | 演算法最直白，沒有 butterfly、bit-reversal、chirp 等容易出錯的步驟 |
| `ReferenceLoudness`：逐取樣差分方程、每個區塊直接加總 | `LoudnessMeter`、`KWeighting` | 不用子區塊累加，濾波器係數由獨立撰寫的程式設計 |
| `RefWavBuilder`：手刻 WAV 位元組 | `WavReader` | 不經過任何 WAV 函式庫，可以刻意做出損壞的檔頭 |

參考實作與正式實作「寫法不同、算的東西相同」。兩者結果一致時，同時寫錯的機率很低。

## 策略三：性質測試

規格要求至少 8 組，目前有 17 組。工具是自寫的 `Prop.ForAll()`（`tests/PracticeMate.Testing.Reference/Prop.cs`）：

```csharp
// tests/PracticeMate.Testing.Reference/Prop.cs
public static void ForAll(int baseSeed, int iterations, Action<Random> body)
{
    var overrideSeed = Environment.GetEnvironmentVariable(SeedEnvironmentVariable);
    if (!string.IsNullOrWhiteSpace(overrideSeed))
    {
        Run(int.Parse(overrideSeed, CultureInfo.InvariantCulture), -1, body);   // ① 只重跑一個種子
        return;
    }
    for (var i = 0; i < iterations; i++)
    {
        Run(SeedFor(baseSeed, i), i, body);                                      // ② 種子固定
    }
}
```

① 失敗訊息會印出種子，設定 `PM_PROP_SEED` 就能只重跑那一個案例，方便除錯。

② 每次迭代的種子由基礎種子推導，**每次執行都相同**。這和 FsCheck 預設的隨機種子不同：CI 不會因為某天抽到壞種子而隨機失敗。代價是不會自動探索新的輸入，也沒有 shrinking（自動縮小失敗案例）。

## 代表性測試走讀一：起音偵測

```csharp
// tests/PracticeMate.Core.Tests/Analysis/OnsetDetectorTests.cs
[Theory]
[InlineData(Articulation.Percussive, 48000)]
[InlineData(Articulation.Plucked, 48000)]
[InlineData(Articulation.Wind, 48000)]
// ...（44.1k、96k）
public void Detects_onsets_within_10ms_with_high_f_measure(Articulation articulation, int fs)
{
    var rng = new Random((int)articulation + fs);
    var notes = IrregularSequence(rng, 30, articulation == Articulation.Wind ? 0.25 : 0.2);  // ①
    var render = PerformanceSynth.Render(notes, new PerformanceOptions
    {
        SampleRate = fs, Articulation = articulation, SnrDb = 40, Seed = fs,
    });
    var detected = OnsetDetector.Detect(SignalGenerator.ToFloat(render.Signal), fs);
    var (f, maxErr, matched) = Evaluate(render.OnsetSamples, detected, fs);                  // ②
    Assert.True(f >= 0.95, $"{articulation}@{fs}：F = {f:F3}（命中 {matched}/…）");           // ③
    Assert.True(maxErr <= 10, $"{articulation}@{fs}：最大起音誤差 {maxErr:F1} ms");
}
```

① 不規則的間隔、不同的音高：規則的節奏可能讓某些錯誤剛好互相抵消。

② `Evaluate()` 依 MIREX 慣例，±50 ms 內視為命中，貪婪地一對一配對，算出 F 值與命中者的最大誤差。這是規格解讀 Q2。

③ 斷言訊息包含實際數值。測試失敗時，CI 的紀錄就能看到「差多少」，不需要在本機重現才知道。

## 代表性測試走讀二：架構規則

```csharp
// tests/PracticeMate.Architecture.Tests/DependencyRuleTests.cs（概念）
// 讀取編譯後組件的 AssemblyRef 表，檢查不得出現的參考
```

`AssemblyInspector`（`tests/PracticeMate.Architecture.Tests/AssemblyInspector.cs`）用 `System.Reflection.Metadata` 的 `PEReader` 直接讀 DLL 的中繼資料表，不需要把組件載入到執行環境，也就不會觸發相依組件的載入。`NAudioUsageTests` 讀取的是 **型別參考** 表，所以能精確到「用了哪個 NAudio 類別」，而不只是「引用了 NAudio」。

這類測試的價值在於：規則寫在測試裡，違反時建置就失敗，不需要靠 code review 抓。

## 效能測試

`tests/PracticeMate.Performance.Tests/PerformanceTests.cs` 以 `[Trait("Category", "Performance")]` 標記：

- 10 分鐘 48 kHz 立體聲合成訊號完整分析 ≤ 10 秒。
- 調音器處理 1000 個 10 ms 區塊，取中位數判定 ≤ 1 ms，同時輸出 p99 與最大值。

以中位數而非 p99 判定，是因為 `dotnet test` 會平行執行各測試組件，p99 容易受 CPU 競爭影響。CI 上效能測試是獨立步驟且 `continue-on-error`，只供參考，因為 GitHub runner 的硬體不固定。

> 📄 依據：設計文件標頭「效能測試於 CI 僅供參考不擋綠燈」（使用者核准）

## 覆蓋範圍與缺口

**有測**：Core 所有模組、所有 ViewModel、架構規則、PNG 匯出的實際繪製、範例錄音與預期結果一致（`SampleRecordingsTests`）。

**沒有自動化測試**：

- `src/PracticeMate.Audio.Windows/` 全部：需要真實音訊裝置。驗收時以 UI Automation 手動驅動實際程式，跑過真實麥克風、真實輸出、強制結束程序的當機測試，但沒有實體拔線。
- XAML 檢視與自繪控制項的外觀：只有 PNG 匯出有測試，其他靠人工檢視與 README 截圖。
- 檔案對話框（`FileDialogService`）：驗收時嘗試以 UI Automation 操作，被中文輸入法擋住，最後以人工確認。
- 真實樂器：所有精度驗證都使用合成訊號。

## 建議補強（尚未實作）

- **建議**：起音偵測的隨機性質測試（不截斷餘音＋殘響＋隨機音量）。目前 0.2% 的極端情況超過 10 ms，因此沒有加入（見 11 章 F4）。
- **建議**：準備一小組真實樂器錄音，人工標註音高與起音，作為回歸測試。
- **建議**：以 Windows 的虛擬音訊裝置（或在 CI 上以 `IMMDeviceEnumerator` 的模擬）測試 `WasapiCaptureDevice` 的拔除路徑。
