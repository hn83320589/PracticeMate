# 04 技術選型

這個專案的技術選型有一個很強的外部約束：**規格禁止使用 DSP 套件**。所以這一章的重點不是「選了哪些套件」，而是「刻意不選哪些、以及剩下的少數套件為什麼被允許」。

## 執行環境與語言

| 技術 | 版本 | 用途 | 選用理由 |
|---|---|---|---|
| .NET SDK | 9.0（`global.json`：`9.0.100`、`rollForward: latestFeature`） | 全部專案 | 📄 規格指定 C# .NET 9 |
| WPF | .NET 9 內建 | UI | 📄 規格指定 WPF + MVVM |
| C# 語言功能 | 預設（C# 13） | primary constructor、collection expression、`record`、`Span<T>` | 🔍 推斷：`Span<T>`／`ReadOnlySpan<T>` 是即時路徑「不配置記憶體」的關鍵工具；`record` 讓跨執行緒共享的結果物件天生不可變 |

## 套件

> 📄 依據：`Directory.Packages.props`（Central Package Management）；`docs/design/architecture.md` §10「外部套件（需確認）」，使用者於 2026-11-06 核准

| 套件 | 版本 | 用途 | 選用理由 | 替代方案 |
|---|---|---|---|---|
| NAudio.Wasapi | 2.2.1 | 只用於裝置列舉、`WasapiCapture`、`WasapiOut`、端點通知 | 📄 規格允許 NAudio 用於裝置 I/O；設計文件限定只用這幾個型別 | 直接 P/Invoke WASAPI COM 介面（大量樣板程式碼）；CSCore（已停止維護） |
| CommunityToolkit.Mvvm | 8.4.0 | `ObservableObject`、`[ObservableProperty]`、`[RelayCommand]` source generator | 📄 規格允許 | 手寫 `INotifyPropertyChanged`；Prism、ReactiveUI（較重） |
| xunit／xunit.runner.visualstudio／Microsoft.NET.Test.Sdk | 2.9.3／3.1.5／17.14.1 | 測試框架 | 📄 規格指定 | NUnit、MSTest |
| Microsoft.Extensions.TimeProvider.Testing | 9.10.0 | `FakeTimeProvider`：測試計時器、錄音檔名、Pump 迴圈 | 📄 設計文件 §10 | 自寫假時鐘 |

### NAudio 的「白名單」

只引用 NAudio 還不夠，規格要求 NAudio 不得用於訊號處理。這由 `tests/PracticeMate.Architecture.Tests/NAudioUsageTests.cs` 強制：它讀取 `PracticeMate.Audio.Windows` 編譯後組件的型別參考，只允許裝置與格式相關的型別，禁止 `NAudio.Dsp`、`*Resampler*`、`*Reader`、`SampleProviders` 等。連位元組轉 float 都由 Core 的 `PcmConverter` 處理，而不是用 NAudio 的轉換器。

> 📄 依據：`src/PracticeMate.Audio.Windows/WasapiCaptureDevice.cs` 註解「位元組→float 由 Core 的 PcmConverter 轉換（不使用 NAudio 的轉換功能）」

## 刻意不用的東西

| 不用 | 改用 | 理由 |
|---|---|---|
| MathNet.Numerics、FftSharp、NWaves | 自寫 FFT、濾波器、重新取樣 | 📄 規格禁止；架構測試檢查任何組件都不得引用 |
| FsCheck（性質測試） | 自寫 `Prop.ForAll`（`tests/PracticeMate.Testing.Reference/Prop.cs`） | 📄 設計文件 §10：「自寫性質測試小工具，避免多一個依賴」；需求只有固定種子與可重現的失敗訊息 |
| DI 容器 | 手動組合根 | 📄 設計文件 §6：元件數量不多 |
| BenchmarkDotNet | 以 xUnit 測試量測中位數與 p99 | 📄 設計文件 §10 列為不使用；🔍 推斷：效能測試要能在 CI 執行並輸出數字，xUnit 已足夠 |
| WPF 圖表套件（LiveCharts、OxyPlot 等） | `FrameworkElement.OnRender` 自繪（`src/PracticeMate.App/Controls/`） | 🔍 推斷：設計文件 §7 指定自訂繪製，未記載理由。可能的理由：波形與頻譜圖需要和 `MinMaxIndex`、8 位元頻譜資料直接對接，並共用時間軸；圖表套件對數十萬點的即時縮放效能不一定足夠 |

## 建置設定

- **Central Package Management**（`Directory.Packages.props`）：版本只寫一處，專案檔只寫套件名稱。
- **`Directory.Build.props`**：`Nullable=enable`、`TreatWarningsAsErrors=true`、`AnalysisLevel=latest-recommended`。所有分析器警告都會讓建置失敗，因此程式碼裡有少數經過說明的 `SuppressMessage`（例如 `App` 類別的 CA1001）。
- **`.editorconfig`**：關閉 CA1716（與 VB 保留字衝突的命名，本專案只用 C#）。

## 版本注意事項

- .NET 9 是 STS（標準期支援）版本，支援期比 LTS 短。🔍 推斷：升級到下一個 LTS 時，主要風險在 WPF 與 NAudio 的相容性，Core 沒有外部相依，影響很小。
- NAudio 2.2.1 是目前專案使用的版本；`WasapiCapture` 在裝置失效時以 `COMException`（`AUDCLNT_E_DEVICE_INVALIDATED`）結束，程式依賴這個行為判斷「裝置被拔除」（`WasapiCaptureDevice.OnRecordingStopped()`）。升級 NAudio 時要重新確認。
