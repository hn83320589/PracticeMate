namespace PracticeMate.Core.Recording;

/// <summary>
/// 在背景定期呼叫 <see cref="CaptureSession.Pump"/>（單一邏輯執行緒：迴圈內循序 await）。
/// 間隔以 <see cref="TimeProvider"/> 計時，測試可替換。
/// </summary>
public sealed class CapturePumpRunner : IAsyncDisposable
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(5);

    private readonly CaptureSession _session;
    private readonly TimeProvider _time;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public CapturePumpRunner(CaptureSession session, TimeProvider time, TimeSpan? interval = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _interval = interval ?? DefaultInterval;
    }

    /// <summary>工作階段結束（Stopped/Faulted）後完成的工作。</summary>
    public Task Completion => _loop ?? Task.CompletedTask;

    public void Start() => _loop ??= Task.Run(RunAsync);

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 正常取消
            }
        }
        _cts.Dispose();
    }

    private async Task RunAsync()
    {
        while (_session.State is CaptureState.Running or CaptureState.Idle)
        {
            _session.Pump();
            await Task.Delay(_interval, _time, _cts.Token).ConfigureAwait(false);
        }
    }
}
