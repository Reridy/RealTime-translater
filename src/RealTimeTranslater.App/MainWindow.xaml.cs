using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using RealTimeTranslater.App.Capture;
using RealTimeTranslater.App.Configuration;
using RealTimeTranslater.App.Ocr;
using RealTimeTranslater.App.Overlay;
using RealTimeTranslater.App.Pipeline;
using RealTimeTranslater.App.Translation;
using RealTimeTranslater.Core.Translation;

namespace RealTimeTranslater.App;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(90)
    };

    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;
    private OverlayWindow? _overlayWindow;
    private TesseractOcrService? _ocrService;
    private string _activeProfileKey = string.Empty;
    private bool _applyingGameProfile;
    private HwndSource? _mainWindowSource;
    private bool _overlayTemporarilyHidden;

    private const int ToggleOverlayHotkeyId = 0x511;
    private const int ToggleRunHotkeyId = 0x512;
    private const int CycleOverlayModeHotkeyId = 0x513;

    private static readonly TargetLanguageOption[] TargetLanguages =
    {
        new("ko", "Korean"),
        new("en", "English"),
        new("ja", "Japanese"),
        new("zh-CN", "Chinese (Simplified)"),
        new("zh-TW", "Chinese (Traditional)"),
        new("es", "Spanish"),
        new("fr", "French"),
        new("de", "German"),
        new("pt", "Portuguese"),
        new("ru", "Russian"),
        new("th", "Thai"),
        new("vi", "Vietnamese"),
        new("id", "Indonesian"),
        new("it", "Italian"),
        new("pl", "Polish"),
        new("tr", "Turkish"),
        new("nl", "Dutch"),
        new("ar", "Arabic")
    };

    public MainWindow()
    {
        InitializeComponent();

        _settings = AppSettings.Load();

        OcrLanguageComboBox.ItemsSource = new[]
        {
            "jpn+eng",
            "jpn",
            "eng"
        };
        OcrLanguageComboBox.SelectedItem = _settings.OcrLanguage;

        TextSourceComboBox.ItemsSource = new[]
        {
            "OCR",
            "Unity Adapter + OCR fallback"
        };
        TextSourceComboBox.SelectedItem =
            TextSourceComboBox.Items.Contains(_settings.TextSource)
                ? _settings.TextSource
                : "OCR";
        UnityDialogueOnlyCheckBox.IsChecked =
            _settings.UnityDialogueOnly;
        UpdateUnityScopeAvailability();

        ProviderComboBox.ItemsSource = new[]
        {
            "Mock",
            "Ollama",
            "LibreTranslate"
        };
        ProviderComboBox.SelectedItem = _settings.Translation.Provider;

        TargetLanguageComboBox.ItemsSource =
            TargetLanguages;
        TargetLanguageComboBox.SelectedItem =
            TargetLanguages.FirstOrDefault(option =>
                string.Equals(
                    option.Code,
                    _settings.Translation.TargetLanguage,
                    StringComparison.OrdinalIgnoreCase))
            ?? TargetLanguages[0];

        OverlayModeComboBox.ItemsSource = new[]
        {
            "Smart",
            "Replace",
            "Subtitle"
        };
        OverlayModeComboBox.SelectedItem = _settings.Overlay.Mode;
        AllowScreenshotsCheckBox.IsChecked = _settings.Overlay.AllowScreenshots;

        ModelTextBox.Text = _settings.Translation.OllamaModel;
        SetEndpointForSelectedProvider();

        RefreshWindows();
    }

    protected override void OnSourceInitialized(
        EventArgs e)
    {
        base.OnSourceInitialized(e);

        var handle =
            new WindowInteropHelper(this).Handle;

        _mainWindowSource =
            HwndSource.FromHwnd(handle);

        _mainWindowSource?.AddHook(
            MainWindowMessageHook);

        var modifiers =
            NativeMethods.ModControl |
            NativeMethods.ModShift |
            NativeMethods.ModNoRepeat;

        _ = NativeMethods.RegisterHotKey(
            handle,
            ToggleOverlayHotkeyId,
            modifiers,
            NativeMethods.VkF8);

        _ = NativeMethods.RegisterHotKey(
            handle,
            ToggleRunHotkeyId,
            modifiers,
            NativeMethods.VkF9);

        _ = NativeMethods.RegisterHotKey(
            handle,
            CycleOverlayModeHotkeyId,
            modifiers,
            NativeMethods.VkF10);
    }

    private IntPtr MainWindowMessageHook(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message !=
            NativeMethods.WmHotkey)
        {
            return IntPtr.Zero;
        }

        handled = true;

        switch (wParam.ToInt32())
        {
            case ToggleOverlayHotkeyId:
                ToggleOverlayVisibility();
                break;

            case ToggleRunHotkeyId:
                _ = Dispatcher.BeginInvoke(
                    async () =>
                    {
                        if (_runCancellation is null)
                        {
                            StartButton_Click(
                                this,
                                new RoutedEventArgs());
                        }
                        else
                        {
                            await StopInternalAsync();
                        }
                    });
                break;

            case CycleOverlayModeHotkeyId:
                CycleOverlayMode();
                break;
        }

        return IntPtr.Zero;
    }

    private void ToggleOverlayVisibility()
    {
        if (_overlayWindow is null)
        {
            StatusTextBlock.Text =
                "Overlay is not running.";
            return;
        }

        _overlayTemporarilyHidden =
            !_overlayTemporarilyHidden;

        if (_overlayTemporarilyHidden)
        {
            _overlayWindow.Hide();
            StatusTextBlock.Text =
                "Overlay hidden · Ctrl+Shift+F8 to show";
        }
        else
        {
            _overlayWindow.Show();
            StatusTextBlock.Text =
                "Overlay visible";
        }
    }

    private void CycleOverlayMode()
    {
        var modes =
            new[]
            {
                "Smart",
                "Replace",
                "Subtitle"
            };

        var current =
            OverlayModeComboBox.SelectedItem?.ToString()
            ?? _settings.Overlay.Mode;

        var index =
            Array.FindIndex(
                modes,
                mode =>
                    string.Equals(
                        mode,
                        current,
                        StringComparison.OrdinalIgnoreCase));

        var next =
            modes[
                (Math.Max(
                    0,
                    index) + 1) %
                modes.Length];

        OverlayModeComboBox.SelectedItem =
            next;

        _settings.Overlay.Mode =
            next;

        SaveActiveGameProfile();
        _settings.Save();

        StatusTextBlock.Text =
            $"Overlay mode: {next}";
    }

    private void MainWindow_Loaded(
        object sender,
        RoutedEventArgs e)
    {
        // Use the larger comfortable layout when possible, but never open
        // taller than the usable desktop. On smaller/scaled displays the
        // settings area becomes scrollable while the status/log panel and
        // Start/Stop controls stay visible.
        var workArea =
            SystemParameters.WorkArea;

        var maximumHeight =
            Math.Max(
                360,
                workArea.Height - 24);

        MinHeight =
            Math.Min(
                MinHeight,
                maximumHeight);

        MaxHeight =
            maximumHeight;

        Height =
            Math.Min(
                640,
                maximumHeight);

        var maximumWidth =
            Math.Max(
                520,
                workArea.Width - 24);

        MinWidth =
            Math.Min(
                MinWidth,
                maximumWidth);

        MaxWidth =
            maximumWidth;

        Width =
            Math.Min(
                760,
                maximumWidth);

        Left =
            workArea.Left +
            Math.Max(
                0,
                (workArea.Width - Width) / 2);

        Top =
            workArea.Top +
            Math.Max(
                0,
                (workArea.Height - Height) / 2);
    }

    protected override void OnClosed(EventArgs e)
    {
        var handle =
            new WindowInteropHelper(this).Handle;

        if (handle != IntPtr.Zero)
        {
            _ = NativeMethods.UnregisterHotKey(
                handle,
                ToggleOverlayHotkeyId);
            _ = NativeMethods.UnregisterHotKey(
                handle,
                ToggleRunHotkeyId);
            _ = NativeMethods.UnregisterHotKey(
                handle,
                CycleOverlayModeHotkeyId);
        }

        _mainWindowSource?.RemoveHook(
            MainWindowMessageHook);
        _mainWindowSource = null;

        CaptureCurrentSettings();
        SaveActiveGameProfile();
        _settings.Save();

        _runCancellation?.Cancel();
        _overlayWindow?.Close();
        _ocrService?.Dispose();
        _httpClient.Dispose();
        base.OnClosed(e);
    }

    private void RefreshWindowsButton_Click(object sender, RoutedEventArgs e)
        => RefreshWindows();

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowComboBox.SelectedItem is not WindowInfo target)
        {
            MessageBox.Show(
                this,
                "Select a target game/window first.",
                "RealTime Translater",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            await StopInternalAsync();

            var ocrLanguage =
                OcrLanguageComboBox.SelectedItem?.ToString()
                ?? _settings.OcrLanguage;

            var providerName =
                ProviderComboBox.SelectedItem?.ToString()
                ?? "Mock";

            var sourceLanguage = ocrLanguage switch
            {
                "eng" => "en",
                "jpn" => "ja",
                _ => "auto"
            };

            var targetLanguage =
                (TargetLanguageComboBox.SelectedItem
                    as TargetLanguageOption)?.Code
                ?? _settings.Translation.TargetLanguage;

            _settings.OcrLanguage = ocrLanguage;
            _settings.TextSource =
                TextSourceComboBox.SelectedItem?.ToString()
                ?? "OCR";
            _settings.UnityDialogueOnly =
                UnityDialogueOnlyCheckBox.IsChecked == true;
            _settings.Overlay.Mode =
                OverlayModeComboBox.SelectedItem?.ToString()
                ?? "Replace";
            _settings.Overlay.AllowScreenshots =
                AllowScreenshotsCheckBox.IsChecked == true;
            _settings.Translation.Provider =
                providerName;
            _settings.Translation.TargetLanguage =
                targetLanguage;

            var configuredEndpoint =
                EndpointTextBox.Text.Trim();

            if (configuredEndpoint.Length > 0)
            {
                if (string.Equals(
                        providerName,
                        "LibreTranslate",
                        StringComparison.OrdinalIgnoreCase))
                {
                    _settings.Translation.LibreTranslateEndpoint =
                        configuredEndpoint;
                }
                else
                {
                    _settings.Translation.OllamaEndpoint =
                        configuredEndpoint;
                }
            }

            var configuredModel =
                ModelTextBox.Text.Trim();

            if (configuredModel.Length > 0)
            {
                _settings.Translation.OllamaModel =
                    configuredModel;
            }

            SaveGameProfile(
                target);
            _settings.Save();

            _ocrService = new TesseractOcrService(
                _settings.OcrDataPath,
                ocrLanguage,
                _settings.MinimumOcrConfidence);

            var provider = CreateTranslationProvider(providerName);

            if (provider is OllamaTranslationProvider ollama)
            {
                StatusTextBlock.Text =
                    "Warming Ollama model...";

                try
                {
                    using var warmupTimeout =
                        new CancellationTokenSource(
                            TimeSpan.FromSeconds(30));

                    await ollama.WarmupAsync(
                        warmupTimeout.Token);
                }
                catch (Exception ex)
                {
                    StatusTextBlock.Text =
                        "Ollama warmup warning: " +
                        ex.Message;
                }
            }

            _overlayWindow = new OverlayWindow(_settings.Overlay);
            _overlayTemporarilyHidden = false;
            _overlayWindow.Show();

            _runCancellation = new CancellationTokenSource();

            var pipeline = new TranslationPipeline(
                target.Handle,
                _ocrService,
                provider,
                _overlayWindow,
                _settings,
                sourceLanguage,
                targetLanguage,
                _settings.TextSource);

            pipeline.StatusChanged += OnPipelineStatusChanged;

            var token = _runCancellation.Token;
            _runTask = Task.Run(async () =>
            {
                try
                {
                    await pipeline.RunAsync(token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        StatusTextBlock.Text = $"Error: {ex.Message}";
                        StartButton.IsEnabled = true;
                        StopButton.IsEnabled = false;
                    });
                }
                finally
                {
                    pipeline.Dispose();
                }
            }, token);

            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            StatusTextBlock.Text = "Starting...";
        }
        catch (Exception ex)
        {
            _ocrService?.Dispose();
            _ocrService = null;

            _overlayWindow?.Close();
            _overlayWindow = null;

            MessageBox.Show(
                this,
                ex.Message,
                "Could not start",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
        => await StopInternalAsync();

    private void AllowScreenshotsCheckBox_Changed(
        object sender,
        RoutedEventArgs e)
    {
        var allowScreenshots = AllowScreenshotsCheckBox.IsChecked == true;
        _settings.Overlay.AllowScreenshots = allowScreenshots;
        _overlayWindow?.SetAllowScreenshots(allowScreenshots);
    }

    private void TextSourceComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (IsLoaded)
            UpdateUnityScopeAvailability();
    }

    private void UnityDialogueOnlyCheckBox_Changed(
        object sender,
        RoutedEventArgs e)
    {
        if (_settings is null)
            return;

        _settings.UnityDialogueOnly =
            UnityDialogueOnlyCheckBox.IsChecked == true;
    }

    private void UpdateUnityScopeAvailability()
    {
        var useUnityAdapter = string.Equals(
            TextSourceComboBox.SelectedItem?.ToString(),
            "Unity Adapter + OCR fallback",
            StringComparison.OrdinalIgnoreCase);

        UnityDialogueOnlyCheckBox.IsEnabled = useUnityAdapter;
    }

    private void ProviderComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (IsLoaded &&
            !_applyingGameProfile)
        {
            SetEndpointForSelectedProvider();
        }
    }

    private void WindowComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_applyingGameProfile)
            return;

        SaveActiveGameProfile();

        if (WindowComboBox.SelectedItem is
            WindowInfo target)
        {
            ApplyGameProfile(
                target);
        }
    }

    private void ApplyGameProfile(
        WindowInfo target)
    {
        _activeProfileKey =
            target.ProfileKey;

        _settings.LastTargetProfileKey =
            target.ProfileKey;

        if (!_settings.GameProfiles.TryGetValue(
                target.ProfileKey,
                out var profile))
        {
            return;
        }

        _applyingGameProfile = true;

        try
        {
            if (OcrLanguageComboBox.Items.Contains(
                    profile.OcrLanguage))
            {
                OcrLanguageComboBox.SelectedItem =
                    profile.OcrLanguage;
            }

            if (TextSourceComboBox.Items.Contains(
                    profile.TextSource))
            {
                TextSourceComboBox.SelectedItem =
                    profile.TextSource;
            }

            UnityDialogueOnlyCheckBox.IsChecked =
                profile.UnityDialogueOnly;

            var targetLanguage =
                TargetLanguages.FirstOrDefault(option =>
                    string.Equals(
                        option.Code,
                        profile.TargetLanguage,
                        StringComparison.OrdinalIgnoreCase));

            if (targetLanguage is not null)
            {
                TargetLanguageComboBox.SelectedItem =
                    targetLanguage;
            }

            if (ProviderComboBox.Items.Contains(
                    profile.Provider))
            {
                ProviderComboBox.SelectedItem =
                    profile.Provider;
            }

            if (!string.IsNullOrWhiteSpace(
                    profile.OllamaModel))
            {
                ModelTextBox.Text =
                    profile.OllamaModel;
            }

            if (OverlayModeComboBox.Items.Contains(
                    profile.OverlayMode))
            {
                OverlayModeComboBox.SelectedItem =
                    profile.OverlayMode;
            }

            AllowScreenshotsCheckBox.IsChecked =
                profile.AllowScreenshots;

            UpdateUnityScopeAvailability();
            SetEndpointForSelectedProvider();
        }
        finally
        {
            _applyingGameProfile = false;
        }
    }

    private void SaveGameProfile(
        WindowInfo target)
        => SaveGameProfile(
            target.ProfileKey);

    private void SaveActiveGameProfile()
    {
        if (string.IsNullOrWhiteSpace(
                _activeProfileKey))
        {
            return;
        }

        SaveGameProfile(
            _activeProfileKey);
    }

    private void SaveGameProfile(
        string profileKey)
    {
        if (string.IsNullOrWhiteSpace(
                profileKey))
        {
            return;
        }

        var targetLanguage =
            (TargetLanguageComboBox.SelectedItem
                as TargetLanguageOption)?.Code
            ?? _settings.Translation.TargetLanguage;

        _settings.GameProfiles[
            profileKey] =
            new GameProfileSettings
            {
                OcrLanguage =
                    OcrLanguageComboBox.SelectedItem?.ToString()
                    ?? _settings.OcrLanguage,
                TextSource =
                    TextSourceComboBox.SelectedItem?.ToString()
                    ?? _settings.TextSource,
                UnityDialogueOnly =
                    UnityDialogueOnlyCheckBox.IsChecked == true,
                TargetLanguage =
                    targetLanguage,
                Provider =
                    ProviderComboBox.SelectedItem?.ToString()
                    ?? _settings.Translation.Provider,
                OllamaModel =
                    ModelTextBox.Text.Trim().Length == 0
                        ? _settings.Translation.OllamaModel
                        : ModelTextBox.Text.Trim(),
                OverlayMode =
                    OverlayModeComboBox.SelectedItem?.ToString()
                    ?? _settings.Overlay.Mode,
                AllowScreenshots =
                    AllowScreenshotsCheckBox.IsChecked == true
            };

        _settings.LastTargetProfileKey =
            profileKey;
    }

    private void CaptureCurrentSettings()
    {
        if (_settings is null)
            return;

        _settings.OcrLanguage =
            OcrLanguageComboBox.SelectedItem?.ToString()
            ?? _settings.OcrLanguage;

        _settings.TextSource =
            TextSourceComboBox.SelectedItem?.ToString()
            ?? _settings.TextSource;

        _settings.UnityDialogueOnly =
            UnityDialogueOnlyCheckBox.IsChecked == true;

        _settings.Overlay.Mode =
            OverlayModeComboBox.SelectedItem?.ToString()
            ?? _settings.Overlay.Mode;

        _settings.Overlay.AllowScreenshots =
            AllowScreenshotsCheckBox.IsChecked == true;

        _settings.Translation.Provider =
            ProviderComboBox.SelectedItem?.ToString()
            ?? _settings.Translation.Provider;

        _settings.Translation.TargetLanguage =
            (TargetLanguageComboBox.SelectedItem
                as TargetLanguageOption)?.Code
            ?? _settings.Translation.TargetLanguage;

        var endpoint =
            EndpointTextBox.Text.Trim();

        if (endpoint.Length > 0)
        {
            if (string.Equals(
                    _settings.Translation.Provider,
                    "LibreTranslate",
                    StringComparison.OrdinalIgnoreCase))
            {
                _settings.Translation.LibreTranslateEndpoint =
                    endpoint;
            }
            else
            {
                _settings.Translation.OllamaEndpoint =
                    endpoint;
            }
        }

        var model =
            ModelTextBox.Text.Trim();

        if (model.Length > 0)
            _settings.Translation.OllamaModel = model;
    }

    private ITranslationProvider CreateTranslationProvider(string providerName)
    {
        var endpoint = EndpointTextBox.Text.Trim();
        var model = ModelTextBox.Text.Trim();

        return providerName switch
        {
            "Ollama" => new OllamaTranslationProvider(
                _httpClient,
                endpoint.Length == 0
                    ? _settings.Translation.OllamaEndpoint
                    : endpoint,
                model.Length == 0
                    ? _settings.Translation.OllamaModel
                    : model),

            "LibreTranslate" => new LibreTranslateProvider(
                _httpClient,
                endpoint.Length == 0
                    ? _settings.Translation.LibreTranslateEndpoint
                    : endpoint),

            _ => new MockTranslationProvider()
        };
    }

    private async Task StopInternalAsync()
    {
        if (_runCancellation is not null)
        {
            _runCancellation.Cancel();

            if (_runTask is not null)
            {
                try
                {
                    await _runTask;
                }
                catch (OperationCanceledException)
                {
                }
            }

            _runCancellation.Dispose();
            _runCancellation = null;
            _runTask = null;
        }

        if (_overlayWindow is not null)
        {
            _overlayWindow.Close();
            _overlayWindow = null;
        }

        if (_ocrService is not null)
        {
            _ocrService.Dispose();
            _ocrService = null;
        }

        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        StatusTextBlock.Text = "Stopped";
    }

    private void OnPipelineStatusChanged(string status)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            StatusTextBlock.Text = status;
        });
    }

    private void RefreshWindows()
    {
        var previousHandle =
            (WindowComboBox.SelectedItem as WindowInfo)?.Handle;

        var windows =
            WindowFinder.GetVisibleWindows();

        WindowComboBox.ItemsSource =
            windows;

        if (previousHandle is not null)
        {
            WindowComboBox.SelectedItem =
                windows.FirstOrDefault(window =>
                    window.Handle ==
                    previousHandle.Value);
        }

        if (WindowComboBox.SelectedItem is null &&
            !string.IsNullOrWhiteSpace(
                _settings.LastTargetProfileKey))
        {
            WindowComboBox.SelectedItem =
                windows.FirstOrDefault(window =>
                    string.Equals(
                        window.ProfileKey,
                        _settings.LastTargetProfileKey,
                        StringComparison.OrdinalIgnoreCase));
        }

        if (WindowComboBox.SelectedItem is null &&
            windows.Count > 0)
        {
            WindowComboBox.SelectedIndex = 0;
        }

        var selected =
            WindowComboBox.SelectedItem
            as WindowInfo;

        StatusTextBlock.Text =
            windows.Count == 0
                ? "No visible target windows found."
                : selected is null
                    ? $"Ready · {windows.Count} visible window(s)"
                    : $"Ready · {windows.Count} visible window(s) · profile {selected.ProcessName}";
    }

    private sealed record TargetLanguageOption(
        string Code,
        string Name)
    {
        public override string ToString()
            => $"{Name} ({Code})";
    }

    private void SetEndpointForSelectedProvider()
    {
        var provider =
            ProviderComboBox.SelectedItem?.ToString()
            ?? _settings.Translation.Provider;

        EndpointTextBox.Text = provider switch
        {
            "LibreTranslate" => _settings.Translation.LibreTranslateEndpoint,
            _ => _settings.Translation.OllamaEndpoint
        };

        ModelTextBox.IsEnabled = provider == "Ollama";
        EndpointTextBox.IsEnabled = provider != "Mock";
    }
}
