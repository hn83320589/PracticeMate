# 07 程式內容導讀

這一章帶你實際讀一遍程式碼。先給閱讀順序，再從進入點出發，完整走一次「錄音」的生命週期，最後逐一介紹各模組。

## 建議閱讀順序

1. `src/PracticeMate.App/App.xaml.cs`：組合根，看所有元件怎麼接起來（5 分鐘）。
2. `src/PracticeMate.Core/Devices/AudioDevices.cs`：Core 與硬體之間的介面，看懂這個就知道邊界在哪。
3. `src/PracticeMate.Core/Buffers/SpscRingBuffer.cs` → `src/PracticeMate.Core/Recording/CaptureSession.cs`：即時路徑的核心。
4. `src/PracticeMate.Core/Analysis/AnalysisPipeline.cs`：分析的總流程，從這裡跳進各演算法（08 章）。
5. 任選一個 ViewModel，建議 `src/PracticeMate.App.ViewModels/AnalysisViewModel.cs`，看 Core 的結果怎麼變成畫面。
6. 最後才看 `src/PracticeMate.App/Controls/` 的自繪控制項，它們只負責畫，邏輯很少。

這個順序是「由內往外」：先看不會變的核心，再看會變的外殼。

## 進入點：組合根

```csharp
// src/PracticeMate.App/App.xaml.cs
protected override void OnStartup(StartupEventArgs e)
{
    base.OnStartup(e);
    _singleInstance = new Mutex(true, SingleInstanceName, out var created);   // ①
    if (!created) { /* 顯示「已經在執行中」後 Shutdown */ }
    DispatcherUnhandledException += OnUnhandledException;

    var time = TimeProvider.System;                                            // ②
    var dispatcher = new WpfDispatcher(Dispatcher);
    _audio = new WasapiAudioSystem();
    var store = new JsonSettingsStore(SettingsPath);
    var settings = new SettingsViewModel(store, _audio, DefaultRecordingFolder);
    _input = new AudioInputController(_audio, time);                           // ③
    // ...
    _shell = new ShellViewModel(
        new TunerViewModel(_input, settings, dispatcher, time),
        new MetronomeViewModel(_audio, settings, dispatcher, time),
        new RecordingViewModel(_input, settings, dispatcher, time),
        new AnalysisViewModel(dialogs, new ChartImageExporter(), settings),
        settings);
    // ...
}
```

① 單一執行個體用具名 `Mutex`。第二個執行個體拿不到擁有權就提示並結束。

② `TimeProvider.System` 只在這裡出現一次，往下全部以參數傳遞。這是讓計時器、錄音檔名、Pump 迴圈都能在測試中用 `FakeTimeProvider` 控制的關鍵。

③ `TunerViewModel` 與 `RecordingViewModel` **共用同一個 `AudioInputController`**。調音器和錄音用的是同一條輸入串流，錄音時調音器照樣有讀數；兩者不會搶同一個裝置。

整個程式的物件圖就在這三十行裡，不需要 DI 容器的設定檔或掃描規則。代價是新增服務時要手動改這裡，但以目前的規模，這反而是優點：相依關係一目了然。

`OnExit()` 的關閉順序也值得一看：先 `_input.DisposeAsync()`（停止錄音並寫入完整檔頭，最多等 3 秒），再處理 ViewModel 與音訊系統。如果順序顛倒，先釋放 WASAPI，錄音檔就收不到最後一段資料。

## 錄音一次的完整生命週期

以下依資料經過的順序，一站一站看。

### 第一站：WASAPI 回呼 → 環狀緩衝區

`WasapiCaptureDevice.OnDataAvailable()`（`src/PracticeMate.Audio.Windows/WasapiCaptureDevice.cs`）在 WASAPI 的執行緒上被呼叫，它用 Core 的 `PcmConverter.ToFloat()` 把位元組轉成 float（寫入預先配置好的 `_buffer`），然後呼叫 sink，也就是 `CaptureSession.OnCaptured()`：

```csharp
// src/PracticeMate.Core/Recording/CaptureSession.cs
/// <summary>擷取執行緒呼叫：只寫入環狀緩衝區，滿了就丟棄並計數。不阻塞、不配置。</summary>
public void OnCaptured(ReadOnlySpan<float> interleaved) => _ring.TryWrite(interleaved);
```

就這一行。即時執行緒上能做的事越少越好，這裡把所有工作都推給消費者。

### 第二站：無鎖環狀緩衝區

```csharp
// src/PracticeMate.Core/Buffers/SpscRingBuffer.cs
public bool TryWrite(ReadOnlySpan<float> block)
{
    var write = _indices.Write;                       // 只有生產者會寫，直接讀即可
    var read = Volatile.Read(ref _indices.Read);      // acquire：看到消費者釋放的空間
    var free = Capacity - (write - read);
    if (block.Length > free)
    {
        Volatile.Write(ref _indices.Overflows, _indices.Overflows + 1);
        Volatile.Write(ref _indices.Dropped, _indices.Dropped + block.Length);
        return false;                                  // ① 整塊丟棄
    }
    var start = (int)(write & _mask);                  // ② 容量為 2 的冪次
    var first = Math.Min(block.Length, Capacity - start);
    block[..first].CopyTo(_buffer.AsSpan(start));
    block[first..].CopyTo(_buffer.AsSpan(0));
    Volatile.Write(ref _indices.Write, write + block.Length); // ③ release
    return true;
}

[StructLayout(LayoutKind.Explicit, Size = 256)]
private struct Indices                                 // ④
{
    [FieldOffset(64)] public long Write;
    [FieldOffset(72)] public long Overflows;
    [FieldOffset(80)] public long Dropped;
    [FieldOffset(192)] public long Read;
}
```

這段程式碼很短，但每一行都有理由：

① **整塊或全不**：空間不足時整個區塊丟掉，而不是寫一半。立體聲是交錯存放（L R L R…），寫一半可能讓之後所有資料的左右聲道錯位。規格要求「丟棄新資料、不覆寫舊資料」。

② 容量進位到 2 的冪次，取餘數變成 `& _mask`，而且讀寫索引是**單調遞增的 64 位元計數器**，不需要處理「讀寫指標相等時是空還是滿」的經典歧義：`write - read` 就是目前的資料量。

③ **發布順序**：先複製資料，最後才用 `Volatile.Write` 更新寫入索引。消費者用 `Volatile.Read` 讀到新索引時，保證也看得到先前寫入的資料（release／acquire 語意）。

④ **避免偽共享（false sharing）**：生產者寫的 `Write` 和消費者寫的 `Read` 放在不同的 64 位元組 cache line（偏移 64 與 192）。若放在一起，兩個核心會不斷互相讓對方的快取失效。

可以改進的地方：`Overflows` 與 `Dropped` 只有生產者寫入，消費者讀取，用 `Volatile.Write` 已足夠；但 `Count`、`FreeSpace` 屬性跨執行緒呼叫時只是近似值，註解有說明，使用時要注意。

### 第三站：Pump，消費者的核心

```csharp
// src/PracticeMate.Core/Recording/CaptureSession.cs
public int Pump()
{
    if (State != CaptureState.Running) return 0;

    // 先讀停止旗標再排空：確保停止前送達的資料都會被寫入
    var stopped = Volatile.Read(ref _deviceStopped);       // ①
    try
    {
        var processed = Drain();
        if (stopped is not null)
        {
            processed += Drain();                            // ②
            Finish(stopped);
        }
        return processed;
    }
    catch (IOException ex) { Fault($"寫入錄音檔失敗：{ex.Message}"); return 0; }
    catch (InvalidOperationException ex) { Fault(ex.Message); return 0; }
}
```

① 順序很重要：**先讀停止旗標，再排空**。如果先排空再讀旗標，可能出現「排空後、讀旗標前」裝置又送來最後一塊資料、然後停止，這一塊就會被漏掉。先讀旗標的話，看到旗標時裝置已經停了，之後的 `Drain()` 一定能取到所有資料。

② 看到停止旗標後再排空一次，然後 `Finish()` 寫入最終檔頭。

`Drain()` 每次最多取 100 ms 的資料（`_pumpBuffer` 大小），依序交給 `StreamingWavWriter.Write()` 和每個 `ICaptureConsumer.Consume()`，並維護 `FramesProcessed` 作為下一塊的起始取樣索引。

**為什麼 `Pump()` 是同步方法？** 這是整個錄音模組可測試性的來源。正式環境由 `CapturePumpRunner`（`src/PracticeMate.Core/Recording/CapturePumpRunner.cs`）每 5 ms 呼叫一次；測試則直接呼叫：

```csharp
// tests/PracticeMate.Core.Tests/Recording/CaptureSessionTests.cs 的典型寫法（簡化）
device.Push(block);                 // FakeCaptureDevice 模擬擷取回呼
device.SimulateDeviceRemoved();     // 模擬拔除
session.Pump();           // 一次確定性的消費
Assert.Equal(CaptureState.Stopped, session.State);
```

沒有 `Thread.Sleep`、沒有「等一下應該就好了」，所以拔除、當機、溢位等情境都能穩定重現。

> 📄 依據：README「設計取捨」「擷取的消費者邏輯是同步的 `Pump()`」

### 第四站：調音器消費資料

`TunerEngine` 實作 `ICaptureConsumer`。它把任意長度的輸入區塊切成固定的 hop（10 ms），每到 hop 邊界就對最近 40 ms 的視窗跑一次音高偵測：

```csharp
// src/PracticeMate.Core/Tuner/TunerEngine.cs
public void Consume(ReadOnlySpan<float> interleaved, int channels, long firstFrame)
{
    var frames = interleaved.Length / channels;
    var f = 0;
    while (f < frames)
    {
        // 一次最多推進到下一個 hop 邊界
        var n = Math.Min(frames - f, _hop - _sinceDetect);
        Shift(n);
        // ... 把 n 框混成單聲道，放到視窗尾端
        f += n;
        _sinceDetect += n;
        if (_sinceDetect >= _hop)
        {
            _sinceDetect = 0;
            if (_filled >= WindowSize) Produce(firstFrame + f);   // ①
        }
    }
}
```

① 每筆讀數帶有 `EndFrame`（以取樣索引計），代表「這筆讀數是根據哪個時間點之前的音訊」。這讓測試可以精確量測延遲（從音高改變的取樣到讀數改變的取樣），而不是量測牆上時間。測試 `Block_size_does_not_change_reading_times` 驗證：不管 WASAPI 一次送 1 ms 還是 37 ms 的資料，讀數產生的時間點都完全相同。

讀數以不可變的 `TunerReading` record 存在 `_latest`，用 `Volatile.Write` 發布。UI 端的 `TunerViewModel` 用 `TimeProvider.CreateTimer()` 每 33 ms 讀一次 `Latest`，兩邊不需要鎖。

### 第五站：停止

使用者按停止 → `AudioInputController.StopAsync()` → `CaptureSession.RequestStop()` → 裝置停止並觸發 `Stopped` 事件 → 事件處理只做一件事：

```csharp
// src/PracticeMate.Core/Recording/CaptureSession.cs
private void OnDeviceStopped(object? sender, StreamStoppedEventArgs e) =>
    Interlocked.CompareExchange(ref _deviceStopped, e, null);
```

`CompareExchange` 讓「第一個停止原因」勝出：如果裝置被拔除後又收到使用者的停止要求，訊息會正確顯示「裝置已被移除」。真正的收尾由下一次 `Pump()` 在消費者執行緒上完成（見第三站）。

## 各模組

### Core／IO：WAV 讀寫

- **職責**：讀取所有 PCM 與浮點格式；回復損壞的檔頭；串流寫入。
- **關鍵類別**：`WavReader.Read()`、`StreamingWavWriter`、`PcmConverter`（`src/PracticeMate.Core/Audio/PcmConverter.cs`）。
- **值得學的寫法**：`PcmConverter` 同時被 WAV 讀取與 WASAPI 擷取使用，讓「24 位元負數的符號延伸」這類容易出錯的邏輯只寫一次、只測一次。
- **可以改進**：整個檔案一次讀進記憶體（60 分鐘立體聲約 1.4 GB）。設計文件 R7 原本規劃用分段陣列避免大型物件堆積（LOH），實作時沒有採用。

### Core／Analysis：分析管線

```csharp
// src/PracticeMate.Core/Analysis/AnalysisPipeline.cs
private static readonly (string Name, double Weight)[] Stages =
[
    ("取樣率轉換", 0.05), ("真峰值", 0.10), ("響度", 0.08), ("音高軌跡", 0.45),
    ("音符切分", 0.02), ("起音偵測", 0.12), ("頻譜圖", 0.15), ("波形", 0.03),
];
// ...
reporter.Begin(1);
var truePeak = TruePeakMeter.Measure(audio, ct);                 // ① 原始取樣率
reporter.Begin(2);
var loudness = LoudnessMeter.Measure(analysisAudio, reporter.For(2), ct);
reporter.Begin(3);
var mono = analysisAudio.MixToMono();
var track = PitchTracker.Analyze(mono, rate, options.Pitch, reporter.For(3), ct);
```

- **職責**：依序執行各分析階段，回報加權進度，支援取消。
- **值得學的寫法**：
  - ① 真峰值傳入的是 `audio`（原始），響度傳入 `analysisAudio`（可能已轉成 48 kHz）。這一行的差異就是規格解讀 Q6 的實作。
  - 進度權重依實測成本設定（音高軌跡占 45%），讓進度條的移動速度大致均勻。`StageReporter.Report()` 在 `lock` 內丟棄小於等於上一次的值，因為 `Parallel.For` 內的各段會亂序回報。
  - `InlineProgress` 是一個同步的 `IProgress<double>`：階段內部回報時不經過 `SynchronizationContext`，只有最外層 ViewModel 提供的 `Progress<T>` 才切回 UI 執行緒，避免每個小進度都排一次 UI 工作。
- **平行化**：階段之間是循序的，平行化在階段內部（`Parallel.For` 以訊框區段切分）。音高軌跡在 `PitchTracker.Analyze()` 中用 `Parallel.For` 的 thread-local 初始化，每條執行緒有自己的 `McLeodPitchDetector`（偵測器內有暫存區，不是執行緒安全的）。

### ViewModels：把結果變成畫面

`AnalysisViewModel.LoadAsync()` 示範了「在邊界處理錯誤」的模式：

```csharp
// src/PracticeMate.App.ViewModels/AnalysisViewModel.cs
try
{
    var read = await Task.Run(() => WavReader.Read(path), token).ConfigureAwait(true);
    // ...
    var result = await AnalysisPipeline.RunAsync(read.Audio, options, progress, token).ConfigureAwait(true);
    // 全部完成才替換畫面上的結果
    _original = read.Audio;
    // ...
    Show(result);
}
catch (OperationCanceledException) { StatusMessage = "已取消分析，畫面保留先前的結果。"; }
catch (UnsupportedWavFormatException ex) { StatusMessage = $"無法開啟檔案：{ex.Message}"; }
catch (IOException ex) { StatusMessage = $"讀取檔案失敗：{ex.Message}"; }
finally { IsBusy = false; }
```

只捕捉預期中的例外類型，每一種都轉成使用者看得懂的訊息；其他例外往上拋到 `App.OnUnhandledException()`。`ConfigureAwait(true)` 是刻意的：await 之後要更新可繫結的屬性，必須回到 UI 執行緒。

**時間軸同步**：`TimelineViewModel`（`src/PracticeMate.App.ViewModels/TimelineViewModel.cs`）只有兩個核心狀態：`StartSample`、`SamplesPerPixel`。`AnalysisView.xaml` 把 `WaveformView` 和 `SpectrogramView` 的 `Timeline` 屬性繫結到同一個實例，所以縮放（`ZoomAt()`）與平移（`Pan()`）天生同步，不需要任何「A 改了通知 B」的程式碼。

### App：自繪控制項

`src/PracticeMate.App/Controls/` 下的控制項都繼承 `FrameworkElement` 並覆寫 `OnRender()`，例如 `WaveformView` 向 `MinMaxIndex.Columns()` 要「每個像素欄的最小最大值」再畫垂直線。重繪成本只跟視窗寬度有關，跟檔案長度無關。

`ChartImageExporter`（`src/PracticeMate.App/Services/WpfServices.cs`）是驗收測試中發現缺陷的地方（見 11 章 F1）：

```csharp
// src/PracticeMate.App/Services/WpfServices.cs
// 不可直接 Render(element)：RenderTargetBitmap 會套用元素在父元素中的位移，
// 圖表不在 (0, 0) 時匯出的圖會位移甚至全空白。改以 VisualBrush 畫在原點。
var visual = new DrawingVisual();
using (var ctx = visual.RenderOpen())
{
    ctx.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
}
bitmap.Render(visual);
```

### Audio.Windows：薄薄一層轉接

三個類別都盡量不含邏輯。`WasapiAudioSystem` 同時實作裝置列舉、裝置工廠，並透過內部的 `EndpointNotifications`（實作 NAudio 的 `IMMNotificationClient`）把 Windows 的端點通知轉成 Core 的事件。判斷「這次停止是因為拔除」的依據是 `COMException` 的 HRESULT `0x88890004`（`AUDCLNT_E_DEVICE_INVALIDATED`）。

**可以改進**：這一層沒有自動化測試（需要真實裝置），驗收 #19 只以假裝置驗證。見 `docs/test-report.md` §6。
