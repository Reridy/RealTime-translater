using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
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

        OverlayModeComboBox.ItemsSource = new[]
        {
            "Replace",
            "Subtitle"
        };
        OverlayModeComboBox.SelectedItem = _settings.Overlay.Mode;
        AllowScreenshotsCheckBox.IsChecked = _settings.Overlay.AllowScreenshots;

        ModelTextBox.Text = _settings.Translation.OllamaModel;
        SetEndpointForSelectedProvider();

        RefreshWindows();
    }

    protected override void OnClosed(EventArgs e)
    {
        CaptureCurrentSettings();
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
            _overlayWindow.Show();

            _runCancellation = new CancellationTokenSource();

            var pipeline = new TranslationPipeline(
                target.Handle,
                _ocrService,
                provider,
                _overlayWindow,
                _settings,
                sourceLanguage,
                _settings.Translation.TargetLanguage,
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
        if (IsLoaded)
            SetEndpointForSelectedProvider();
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

        var windows = WindowFinder.GetVisibleWindows();
        WindowComboBox.ItemsSource = windows;

        if (previousHandle is not null)
        {
            WindowComboBox.SelectedItem =
                windows.FirstOrDefault(x => x.Handle == previousHandle.Value);
        }

        if (WindowComboBox.SelectedItem is null && windows.Count > 0)
            WindowComboBox.SelectedIndex = 0;

        StatusTextBlock.Text =
            windows.Count == 0
                ? "No visible target windows found."
                : $"Ready · {windows.Count} visible window(s)";
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
