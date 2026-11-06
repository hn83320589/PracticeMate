using System.IO;
using System.Windows;
using System.Windows.Threading;
using PracticeMate.App.Services;
using PracticeMate.App.ViewModels;
using PracticeMate.App.ViewModels.Services;
using PracticeMate.Audio.Windows;
using PracticeMate.Core.Recording;

namespace PracticeMate.App;

/// <summary>組合根：建立 WASAPI、設定檔、ViewModel 並連接；確保單一執行個體。</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "Application 的生命週期即程序生命週期，資源在 OnExit 釋放。")]
public partial class App : Application
{
    private const string SingleInstanceName = "PracticeMate.SingleInstance";

    private Mutex? _singleInstance;
    private WasapiAudioSystem? _audio;
    private AudioInputController? _input;
    private ShellViewModel? _shell;

    public static string SettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PracticeMate", "settings.json");

    public static string DefaultRecordingFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Music", "PracticeMate");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _singleInstance = new Mutex(true, SingleInstanceName, out var created);
        if (!created)
        {
            MessageBox.Show("PracticeMate 已經在執行中。", "PracticeMate", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        DispatcherUnhandledException += OnUnhandledException;

        var time = TimeProvider.System;
        var dispatcher = new WpfDispatcher(Dispatcher);
        _audio = new WasapiAudioSystem();
        var store = new JsonSettingsStore(SettingsPath);
        var settings = new SettingsViewModel(store, _audio, DefaultRecordingFolder);
        _input = new AudioInputController(_audio, time);
        var dialogs = new FileDialogService(() => settings.EffectiveRecordingFolder);

        _shell = new ShellViewModel(
            new TunerViewModel(_input, settings, dispatcher, time),
            new MetronomeViewModel(_audio, settings, dispatcher, time),
            new RecordingViewModel(_input, settings, dispatcher, time),
            new AnalysisViewModel(dialogs, new ChartImageExporter(), settings),
            settings);

        var window = new MainWindow { DataContext = _shell };
        MainWindow = window;
        window.Show();
        if (store.LastLoadError is { } error)
        {
            MessageBox.Show(window, error, "PracticeMate", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // 啟動時可直接開啟檔案：PracticeMate.exe 錄音.wav（也用於產生 README 截圖）
        if (e.Args.Length > 0 && File.Exists(e.Args[0]))
        {
            _shell.CurrentPage = Page.Analysis;
            _ = _shell.Analysis.LoadAsync(e.Args[0]);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 正常結束：停止錄音並寫入完整檔頭
        _input?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        _shell?.Tuner.Dispose();
        _shell?.Metronome.Dispose();
        _shell?.Recording.Dispose();
        _shell?.Analysis.Dispose();
        _audio?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(MainWindow, $"發生未預期的錯誤：{e.Exception.Message}\n\n錄音資料已定期寫入檔案，重新開啟後仍可讀取。",
            "PracticeMate", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
