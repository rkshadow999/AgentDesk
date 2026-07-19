using AgentDesk.App.Bridge;
using AgentDesk.App.Bootstrap;
using AgentDesk.App.Attachments;
using AgentDesk.App.Automation;
using AgentDesk.App.Cloud;
using AgentDesk.App.Maintenance;
using AgentDesk.App.Notifications;
using AgentDesk.App.Windowing;
using AgentDesk.Platform.Windows.Backup;
using AgentDesk.Platform.Windows.Settings;
using AgentDesk.Updater.Core;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Windows.System;
using Windows.Storage.Pickers;

namespace AgentDesk.App;

public sealed partial class MainWindow : Window
{
#if DEBUG
    private const bool AllowDevelopmentWebAssetFallbacks = true;
#else
    private const bool AllowDevelopmentWebAssetFallbacks = false;
#endif

    private readonly AppLaunchOptions _launchOptions;
    private readonly string? _launchError;
    private readonly MinimumWindowSize _minimumWindowSize;
    private readonly AgentDeskHostController _hostController;
    private readonly NativeImageAttachmentStore _imageAttachmentStore;
    private readonly WindowShutdownCoordinator _shutdownCoordinator;
    private readonly WebCommandDispatcher _commandDispatcher;
    private readonly AgentDeskUpdateCoordinator _updateCoordinator;
    private readonly AgentDeskBackgroundUpdateMonitor _backgroundUpdateMonitor;
    private readonly AgentDeskMaintenanceCoordinator _maintenanceCoordinator;
    private readonly AgentDeskCloudCoordinator _cloudCoordinator;
    private readonly WindowsAutomationCoordinator _windowsAutomationCoordinator;
    private readonly WindowsNotificationActivationCoordinator _notificationActivationCoordinator;
    private readonly WebSurfaceDefinition _workbenchSurface;
    private readonly WebSurfaceDefinition _inspectorSurface;
    private readonly NativeStringResources _strings;
    private readonly CancellationTokenSource _windowShutdown = new();
    private readonly ContentDialogQueue _contentDialogQueue = new();
    private readonly HashSet<CoreWebView2> _loadedSurfaces = [];
    private readonly WebDocumentCommandGate _documentCommandGate = new();
    private CoreWebView2Environment? _webViewEnvironment;
    private bool _isInitializing;
    private bool _isClosing;
    private bool _localResourcesDisposed;
    private bool _webViewsClosedForMaintenance;

    public MainWindow(AppLaunchOptions launchOptions, string? launchError = null)
    {
        _launchOptions = launchOptions;
        _launchError = launchError;
        var notificationService = new WindowsUserNotificationService();
        var cloudPolicyGate = new AgentDeskCloudPolicyGate();
        _hostController = new AgentDeskHostController(
            CreateHostOptions(launchOptions, cloudPolicyGate),
            notificationService,
            RequestExtensionApprovalAsync);
        var maintenanceRuntime = CreateMaintenanceRuntime(launchOptions);
        _updateCoordinator = maintenanceRuntime.UpdateCoordinator;
        _backgroundUpdateMonitor = new AgentDeskBackgroundUpdateMonitor(
            new AgentDeskBackgroundUpdateCheckCoordinator(
                _updateCoordinator,
                PublishMaintenanceEventAsync),
            AgentDeskBackgroundUpdateMonitorOptions.CreateDefault(
                maintenanceRuntime.Options.PackageMode));
        _maintenanceCoordinator = new AgentDeskMaintenanceCoordinator(
            _hostController,
            new SessionDocumentFileStore(),
            new AgentDeskBackupService(),
            _updateCoordinator,
            maintenanceRuntime.Options,
            PublishMaintenanceEventAsync,
            PrepareForRestoreAsync,
            RestartApplicationAsync,
            RequestApplicationExitAsync);
        _cloudCoordinator = new AgentDeskCloudCoordinator(
            new AgentDeskCloudDesktopService(),
            _hostController,
            PublishCloudEventAsync,
            cloudPolicyGate,
            ownsService: true);
        _windowsAutomationCoordinator = new WindowsAutomationCoordinator(
            new WindowsUiAutomationExecutor(),
            _hostController.IsWindowsAutomationEnabledAsync,
            RequestWindowsAutomationApprovalAsync,
            PublishWindowsAutomationEventAsync);
        _shutdownCoordinator = new WindowShutdownCoordinator(
            DisposeRuntimeAsync,
            maxAttempts: 3,
            retryDelay: TimeSpan.FromMilliseconds(250));
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _imageAttachmentStore = new NativeImageAttachmentStore(Path.Combine(
            localAppData,
            "AgentDesk",
            "AttachmentStaging"));
        _commandDispatcher = new WebCommandDispatcher(
            ForwardWebCommandAsync,
            PickWorkspaceAsync,
            _hostController.UpdateWorkspaceAsync,
            SetModalStateAsync,
            PickMaintenanceFileAsync,
            _maintenanceCoordinator.HandleAsync,
            PromptProviderCredentialAsync,
            _hostController.SaveProviderAsync,
            PickImageAttachmentsAsync,
            _imageAttachmentStore,
            PublishImageAttachmentEventAsync);
        _workbenchSurface = WebSurfacePolicy.Create(WebSurfaceKind.Workbench, localAppData);
        _inspectorSurface = WebSurfacePolicy.Create(WebSurfaceKind.Inspector, localAppData);
        _hostController.EventProduced += HostController_EventProduced;

        InitializeComponent();
        _strings = new NativeStringResources();
        ConfigureLocalizedShell();
        ConfigureWindowChrome();
        _minimumWindowSize = new(
            this,
            minimumWidth: 1024,
            minimumHeight: 700,
            initialWidth: 1280,
            initialHeight: 800);
        ConfigureWorkspaceStatus();
        AppWindow.Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
        _notificationActivationCoordinator = new WindowsNotificationActivationCoordinator(
            notificationService,
            cancellationToken =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return RunOnUiThreadAsync(Activate);
            },
            _hostController.OpenIndexedSessionAsync,
            _ => Debug.WriteLine("AgentDesk could not process a notification activation."));
        _notificationActivationCoordinator.Start();
    }

    private Task ForwardWebCommandAsync(
        WebCommand command,
        CancellationToken cancellationToken)
    {
        if (command is PromptWebCommand { ResolvedAttachments.Count: > 0 } prompt)
        {
            command = new PromptWebCommand(
                prompt.Text,
                prompt.ExecutionProfile,
                prompt.NativeRiskAcknowledged,
                prompt.WorkspaceGeneration,
                prompt.SessionMode,
                AttachmentItems: prompt.ResolvedAttachments);
        }

        if (_windowsAutomationCoordinator.TryHandle(
                command,
                cancellationToken,
                out var automationHandling))
        {
            return automationHandling;
        }

        return command switch
        {
            CloudProfileSaveRemoteWebCommand value => HandleCloudRemoteProfileAsync(
                value,
                cancellationToken),
            CloudPairingExportWebCommand value => HandleCloudPairingExportAsync(
                value,
                cancellationToken),
            CloudPairingImportWebCommand value => HandleCloudPairingImportAsync(
                value,
                cancellationToken),
            CloudSessionExportWebCommand value => HandleCloudSessionExportAsync(
                value,
                cancellationToken),
            CloudWebCommand value => _cloudCoordinator.HandleAsync(value, cancellationToken),
            _ => _hostController.HandleAsync(command, cancellationToken),
        };
    }

    private async Task HandleCloudRemoteProfileAsync(
        CloudProfileSaveRemoteWebCommand command,
        CancellationToken cancellationToken)
    {
        var accessToken = await PromptNativeSecretAsync(
            Text("CloudAccessTokenTitle"),
            Text("CloudAccessTokenPrompt"),
            value => value.Length is > 0 and <= 8 * 1024 && !value.Any(char.IsWhiteSpace),
            Text("CloudAccessTokenInvalid"),
            cancellationToken);
        if (accessToken is null)
        {
            await _cloudCoordinator.CancelAsync(command, cancellationToken);
            return;
        }

        try
        {
            await _cloudCoordinator
                .SaveRemoteProfileAsync(command, accessToken, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            accessToken = null;
        }
    }

    private async Task HandleCloudPairingExportAsync(
        CloudPairingExportWebCommand command,
        CancellationToken cancellationToken)
    {
        var passphrase = await PromptPairingPassphraseAsync(cancellationToken);
        if (passphrase is null)
        {
            await _cloudCoordinator.CancelAsync(command, cancellationToken);
            return;
        }
        try
        {
            var nativePath = await PickCloudPairingFileAsync(save: true, cancellationToken);
            if (nativePath is null)
            {
                await _cloudCoordinator.CancelAsync(command, cancellationToken);
                return;
            }
            await _cloudCoordinator
                .ExportPairingAsync(command, nativePath, passphrase, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            Array.Clear(passphrase);
        }
    }

    private async Task HandleCloudSessionExportAsync(
        CloudSessionExportWebCommand command,
        CancellationToken cancellationToken)
    {
        var nativePath = await PickMaintenanceFileAsync(
            new DesktopFileDialogRequest(
                DesktopFileDialogKind.Save,
                "session-export",
                "AgentDesk-session.agentdesk-session.json",
                ".json"),
            cancellationToken);
        if (nativePath is null)
        {
            await _cloudCoordinator.CancelAsync(command, cancellationToken);
            return;
        }
        await _cloudCoordinator
            .ExportSessionAsync(command, nativePath, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task HandleCloudPairingImportAsync(
        CloudPairingImportWebCommand command,
        CancellationToken cancellationToken)
    {
        var nativePath = await PickCloudPairingFileAsync(save: false, cancellationToken);
        if (nativePath is null)
        {
            await _cloudCoordinator.CancelAsync(command, cancellationToken);
            return;
        }
        var passphrase = await PromptPairingPassphraseAsync(cancellationToken);
        if (passphrase is null)
        {
            await _cloudCoordinator.CancelAsync(command, cancellationToken);
            return;
        }
        try
        {
            await _cloudCoordinator
                .ImportPairingAsync(command, nativePath, passphrase, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            Array.Clear(passphrase);
        }
    }

    private async Task<char[]?> PromptPairingPassphraseAsync(
        CancellationToken cancellationToken)
    {
        var value = await PromptNativeSecretAsync(
            Text("CloudPairingPassphraseTitle"),
            Text("CloudPairingPassphrasePrompt"),
            candidate => candidate.Length is >= 16 and <= 1_024 &&
                !string.IsNullOrWhiteSpace(candidate),
            Text("CloudPairingPassphraseInvalid"),
            cancellationToken);
        return value?.ToCharArray();
    }

    private Task<string?> PromptProviderCredentialAsync(CancellationToken cancellationToken) =>
        PromptNativeSecretAsync(
            Text("ProviderCredentialTitle"),
            Text("ProviderCredentialPrompt"),
            value => value.Length is > 0 and <= 8 * 1024 && !value.Any(char.IsWhiteSpace),
            Text("ProviderCredentialInvalid"),
            cancellationToken);

    private Task<string?> PromptNativeSecretAsync(
        string title,
        string prompt,
        Func<string, bool> validate,
        string validationMessage,
        CancellationToken cancellationToken) =>
        _contentDialogQueue.EnqueueAsync(
            token => ShowNativeSecretAsync(
                title,
                prompt,
                validate,
                validationMessage,
                token),
            shutdownResult: (string?)null,
            cancellationToken);

    private async Task<string?> ShowNativeSecretAsync(
        string title,
        string prompt,
        Func<string, bool> validate,
        string validationMessage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var passwordBox = new PasswordBox
        {
            Header = prompt,
            PasswordRevealMode = PasswordRevealMode.Peek,
            MaxLength = 8 * 1024,
        };
        var validation = new TextBlock
        {
            Text = validationMessage,
            Foreground = ResourceBrush("AgentDeskErrorBrush"),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(passwordBox);
        content.Children.Add(validation);
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = title,
            Content = content,
            PrimaryButtonText = Text("CloudConfirm"),
            CloseButtonText = Text("CloudCancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        void ValidatePrimary(ContentDialog _, ContentDialogButtonClickEventArgs args)
        {
            if (!validate(passwordBox.Password))
            {
                args.Cancel = true;
                validation.Visibility = Visibility.Visible;
                passwordBox.Focus(FocusState.Programmatic);
            }
        }
        dialog.PrimaryButtonClick += ValidatePrimary;
        try
        {
            using var registration = cancellationToken.Register(() =>
                _ = DispatcherQueue.TryEnqueue(dialog.Hide));
            var result = await dialog.ShowAsync();
            cancellationToken.ThrowIfCancellationRequested();
            return result is ContentDialogResult.Primary ? passwordBox.Password : null;
        }
        finally
        {
            dialog.PrimaryButtonClick -= ValidatePrimary;
            passwordBox.Password = string.Empty;
        }
    }

    private async Task<string?> PickCloudPairingFileAsync(
        bool save,
        CancellationToken cancellationToken)
    {
        const string extension = ".agentdesk-pairing";
        cancellationToken.ThrowIfCancellationRequested();
        var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (save)
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = "AgentDesk-recovery-key",
                DefaultFileExtension = extension,
            };
            picker.FileTypeChoices.Add(Text("CloudPairingFiles"), [extension]);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);
            var selected = await picker.PickSaveFileAsync();
            cancellationToken.ThrowIfCancellationRequested();
            return selected?.Path;
        }

        var openPicker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        openPicker.FileTypeFilter.Add(extension);
        WinRT.Interop.InitializeWithWindow.Initialize(openPicker, windowHandle);
        var file = await openPicker.PickSingleFileAsync();
        cancellationToken.ThrowIfCancellationRequested();
        return file?.Path;
    }

    private static AgentDeskHostOptions CreateHostOptions(
        AppLaunchOptions launchOptions,
        AgentDeskCloudPolicyGate cloudPolicyGate)
    {
        var wslEnginePath = Path.Combine(AppContext.BaseDirectory, "wsl", "agentdesk-engine");
        var wslExecutablePath = Path.Combine(Environment.SystemDirectory, "wsl.exe");
        return new AgentDeskHostOptions(launchOptions.WorkspacePath)
        {
            WslEnginePath = wslEnginePath,
            IsWslStrictAvailable = WslStrictAvailability.IsAvailable(
                wslExecutablePath,
                wslEnginePath),
            CloudPolicyGate = cloudPolicyGate,
        };
    }

    private static (
        AgentDeskUpdateCoordinator UpdateCoordinator,
        AgentDeskMaintenanceOptions Options) CreateMaintenanceRuntime(
        AppLaunchOptions launchOptions)
    {
        var installationDirectory = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var installationParent = Directory.GetParent(installationDirectory)?.FullName ??
            throw new InvalidOperationException("The AgentDesk installation directory is invalid.");
        var stateDirectory = Path.Combine(
            installationParent,
            $".{Path.GetFileName(installationDirectory)}-AgentDesk-Updates");
        var version = CurrentSemanticVersion();
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => UpdateArchitecture.X64,
            Architecture.Arm64 => UpdateArchitecture.Arm64,
            _ => throw new PlatformNotSupportedException(
                "AgentDesk updates support only x64 and ARM64 Windows processes."),
        };
        var restartArguments = BuildRestartArguments(launchOptions);
        var updateOptions = AgentDeskUpdateDefaults.Create(
            version,
            architecture,
            stateDirectory,
            installationDirectory,
            restartArguments);
        var packageMode = IsPackagedApplication()
            ? AgentDeskPackageMode.Msix
            : AgentDeskPackageMode.Portable;
        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgentDesk");
        return (
            new AgentDeskUpdateCoordinator(updateOptions),
            new AgentDeskMaintenanceOptions(
                dataDirectory,
                packageMode,
                version.ToString(),
                Environment.ProcessId));
    }

    private static SemanticVersion CurrentSemanticVersion()
    {
        var assembly = typeof(MainWindow).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        return ResolveSemanticVersion(informationalVersion, assembly.GetName().Version);
    }

    internal static SemanticVersion ResolveSemanticVersion(
        string? informationalVersion,
        Version? assemblyVersion)
    {
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            if (SemanticVersion.TryParse(informationalVersion, out var semanticVersion))
            {
                return semanticVersion;
            }
            throw new InvalidOperationException(
                "The AgentDesk informational version is not valid SemVer.");
        }

        var candidate = assemblyVersion is null
            ? "0.1.0"
            : $"{Math.Max(0, assemblyVersion.Major)}.{Math.Max(0, assemblyVersion.Minor)}." +
                $"{Math.Max(0, assemblyVersion.Build)}";
        return SemanticVersion.Parse(candidate);
    }

    private static IReadOnlyList<string> BuildRestartArguments(AppLaunchOptions launchOptions)
    {
        var arguments = new List<string>();
        if (!string.IsNullOrWhiteSpace(launchOptions.WorkspacePath))
        {
            arguments.Add("--workspace");
            arguments.Add(launchOptions.WorkspacePath);
        }
#if DEBUG
        if (!string.IsNullOrWhiteSpace(launchOptions.WebRoot))
        {
            arguments.Add("--web-root");
            arguments.Add(launchOptions.WebRoot);
        }
#endif
        return arguments;
    }

    private static bool IsPackagedApplication()
    {
        try
        {
            _ = Windows.ApplicationModel.Package.Current.Id.Name;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (COMException)
        {
            return false;
        }
    }

    private void ConfigureWindowChrome()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(20, 127, 127, 127);
    }

    private void ConfigureWorkspaceStatus()
    {
        WorkspaceStatusText.Text = string.IsNullOrWhiteSpace(_launchOptions.WorkspacePath)
            ? Text("WorkspaceNotSelected")
            : _launchOptions.WorkspacePath;
        ToolTipService.SetToolTip(WorkspaceStatusText, WorkspaceStatusText.Text);
    }

    private void ConfigureLocalizedShell()
    {
        AutomationProperties.SetName(WorkbenchWebView, Text("WorkbenchAutomationName"));
        AutomationProperties.SetName(InspectorWebView, Text("InspectorAutomationName"));
        AutomationProperties.SetName(ReloadButton, Text("Reload"));
        ToolTipService.SetToolTip(ReloadButton, Text("Reload"));
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (_launchError is not null)
        {
            ShowError(_launchError);
            return;
        }

        await InitializeWorkbenchAsync();
    }

    private async void ReloadButton_Click(object sender, RoutedEventArgs e)
    {
        await InitializeWorkbenchAsync();
    }

    private async Task InitializeWorkbenchAsync()
    {
        if (_isInitializing)
        {
            return;
        }

        _isInitializing = true;
        ShowLoading();

        try
        {
            await AgentDeskEnginePolicy.EnsureAsync(cancellationToken: _windowShutdown.Token);
            using (var policyTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                       _windowShutdown.Token))
            {
                policyTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                try
                {
                    await _cloudCoordinator.InitializePolicyAsync(policyTimeout.Token);
                }
                catch (Exception) when (!(_windowShutdown.IsCancellationRequested))
                {
                    // A saved remote profile remains fail-closed when policy refresh is unavailable.
                }
            }
            var indexPath = WebAssetLocator.FindIndexPath(
                AppContext.BaseDirectory,
                _launchOptions.WebRoot,
                AllowDevelopmentWebAssetFallbacks);
            if (indexPath is null)
            {
                ShowError(WebAssetLocator.MissingAssetsMessage);
                return;
            }

            var assetDirectory = Path.GetDirectoryName(indexPath)!;
            _loadedSurfaces.Clear();
            await WebViewStartupCoordinator.InitializeSequentiallyAsync(
                () => InitializeWebViewAsync(
                    WorkbenchWebView,
                    _workbenchSurface,
                    assetDirectory),
                () => InitializeWebViewAsync(
                    InspectorWebView,
                    _inspectorSurface,
                    assetDirectory));
        }
        catch (Exception exception)
        {
            ShowError(FormatText("DesktopStartFailed", exception.Message));
        }
        finally
        {
            _isInitializing = false;
        }
    }

    private async Task InitializeWebViewAsync(
        WebView2 webView,
        WebSurfaceDefinition surface,
        string assetDirectory)
    {
        await WebViewStartupCoordinator.InitializeOnceAsync(
            isInitialized: () => webView.CoreWebView2 is not null,
            initializeAsync: async () =>
            {
                _webViewEnvironment ??= await CoreWebView2Environment.CreateWithOptionsAsync(
                    browserExecutableFolder: null,
                    userDataFolder: surface.UserDataFolder,
                    options: null);
                var controllerOptions =
                    _webViewEnvironment.CreateCoreWebView2ControllerOptions();
                controllerOptions.ProfileName = surface.ProfileName;
                await webView.EnsureCoreWebView2Async(
                    _webViewEnvironment,
                    controllerOptions);
            },
            configure: () => ConfigureWebView(webView, surface, assetDirectory));
    }

    private void ConfigureWebView(
        WebView2 webView,
        WebSurfaceDefinition surface,
        string assetDirectory)
    {
        var coreWebView = webView.CoreWebView2 ??
            throw new InvalidOperationException("WebView2 initialization did not produce a core view.");
        coreWebView.SetVirtualHostNameToFolderMapping(
            surface.VirtualHostName,
            assetDirectory,
            CoreWebView2HostResourceAccessKind.DenyCors);
        coreWebView.NavigationStarting -= CoreWebView2_NavigationStarting;
        coreWebView.NavigationStarting += CoreWebView2_NavigationStarting;
        coreWebView.NavigationCompleted -= CoreWebView2_NavigationCompleted;
        coreWebView.NavigationCompleted += CoreWebView2_NavigationCompleted;
        coreWebView.NewWindowRequested -= CoreWebView2_NewWindowRequested;
        coreWebView.NewWindowRequested += CoreWebView2_NewWindowRequested;
        coreWebView.WebMessageReceived -= CoreWebView2_WebMessageReceived;
        coreWebView.WebMessageReceived += CoreWebView2_WebMessageReceived;
        coreWebView.Settings.IsStatusBarEnabled = false;
        webView.Source = surface.Source;
    }

    private void CoreWebView2_NavigationStarting(
        CoreWebView2 sender,
        CoreWebView2NavigationStartingEventArgs args)
    {
        var surface = ResolveSurface(sender);
        if (surface is null || !WebSurfacePolicy.IsAllowedSource(surface, args.Uri))
        {
            args.Cancel = true;
            return;
        }

        _documentCommandGate.BeginNavigation(surface.ProfileName, args.NavigationId);
    }

    private void CoreWebView2_NavigationCompleted(
        CoreWebView2 sender,
        CoreWebView2NavigationCompletedEventArgs args)
    {
        if (!args.IsSuccess)
        {
            // Reload reconfigures the existing WebView and starts a fresh token generation.
            ShowError(FormatText("DesktopNavigationFailed", args.WebErrorStatus));
            return;
        }

        var surface = ResolveSurface(sender);
        if (surface is null)
        {
            return;
        }

        var documentToken = _documentCommandGate.CompleteNavigation(
            surface.ProfileName,
            args.NavigationId);
        if (documentToken is null)
        {
            return;
        }
        sender.PostWebMessageAsJson(WebMessageProtocol.SerializeDocumentToken(documentToken));

        _loadedSurfaces.Add(sender);
        if (_loadedSurfaces.Count < 2)
        {
            return;
        }

        WorkbenchWebView.Visibility = Visibility.Visible;
        InspectorWebView.Visibility = Visibility.Visible;
        LoadingPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        SetReadyStatus(Text("InterfaceReady"));
    }

    private async ValueTask DisposeRuntimeAsync()
    {
        await _backgroundUpdateMonitor.DisposeAsync().ConfigureAwait(false);
        await _hostController.DisposeAsync().ConfigureAwait(false);
        await _commandDispatcher.DisposeImageAttachmentsAsync().ConfigureAwait(false);
    }

    private async void CoreWebView2_NewWindowRequested(
        CoreWebView2 sender,
        CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;

        if (!args.IsUserInitiated || !TryCreateExternalLinkUri(args.Uri, out var uri))
        {
            return;
        }

        var deferral = args.GetDeferral();
        try
        {
            await Launcher.LaunchUriAsync(uri);
        }
        catch (Exception)
        {
            // Keep untrusted link failures outside the WebView and application process.
        }
        finally
        {
            deferral.Complete();
        }
    }

    private static bool TryCreateExternalLinkUri(string? rawUri, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(rawUri) || rawUri.Length > 4096)
        {
            return false;
        }

        if (!Uri.TryCreate(rawUri, UriKind.Absolute, out var candidate) ||
            (candidate.Scheme != Uri.UriSchemeHttps && candidate.Scheme != Uri.UriSchemeHttp) ||
            string.IsNullOrWhiteSpace(candidate.Host) ||
            !string.IsNullOrEmpty(candidate.UserInfo))
        {
            return false;
        }

        uri = candidate;
        return true;
    }

    private async void CoreWebView2_WebMessageReceived(
        CoreWebView2 sender,
        CoreWebView2WebMessageReceivedEventArgs args)
    {
        var surface = ResolveSurface(sender);
        if (_isClosing ||
            surface is null ||
            !WebSurfacePolicy.IsAllowedSource(surface, args.Source))
        {
            return;
        }

        try
        {
            var command = _documentCommandGate.ParseCurrentCommand(
                surface.ProfileName,
                args.WebMessageAsJson);
            if (ReferenceEquals(surface, _inspectorSurface) && command is not UiReadyWebCommand)
            {
                throw new InvalidDataException("The inspector surface is read-only.");
            }
            await _commandDispatcher.DispatchAsync(command, _windowShutdown.Token);
        }
        catch (OperationCanceledException) when (_windowShutdown.IsCancellationRequested)
        {
        }
        catch (InvalidDataException)
        {
            var errorEvent = WebMessageProtocol.TryCreateCommandErrorEvent(
                args.WebMessageAsJson,
                Text("UnsupportedDesktopMessage"));
            PostWebEvent(
                errorEvent ??
                new EngineStatusWebEvent("error", Text("UnsupportedDesktopMessage")));
        }
        catch (Exception)
        {
            var errorEvent = WebMessageProtocol.TryCreateCommandErrorEvent(
                args.WebMessageAsJson,
                Text("DesktopCommandFailed"));
            PostWebEvent(
                errorEvent ??
                new EngineStatusWebEvent("error", Text("DesktopCommandFailed")));
        }
    }

    private WebSurfaceDefinition? ResolveSurface(CoreWebView2 sender)
    {
        var profileName = sender.Profile.ProfileName;
        if (WebSurfacePolicy.IsSurfaceProfile(_workbenchSurface, profileName))
        {
            return _workbenchSurface;
        }
        if (WebSurfacePolicy.IsSurfaceProfile(_inspectorSurface, profileName))
        {
            return _inspectorSurface;
        }
        return null;
    }

    private async Task<string?> PickWorkspaceAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            CommitButtonText = Text("SelectWorkspace"),
        };
        picker.FileTypeFilter.Add("*");
        var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);

        var folder = await picker.PickSingleFolderAsync();
        cancellationToken.ThrowIfCancellationRequested();
        return folder?.Path;
    }

    private async Task<string?> PickMaintenanceFileAsync(
        DesktopFileDialogRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var fileTypeName = request.Operation.StartsWith("session-", StringComparison.Ordinal)
            ? Text("SessionTransferFiles")
            : Text("BackupArchiveFiles");

        if (request.Kind is DesktopFileDialogKind.Save)
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = request.SuggestedFileName,
                DefaultFileExtension = request.FileExtension,
            };
            picker.FileTypeChoices.Add(fileTypeName, [request.FileExtension]);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);
            var file = await picker.PickSaveFileAsync();
            cancellationToken.ThrowIfCancellationRequested();
            return file?.Path;
        }

        var openPicker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        openPicker.FileTypeFilter.Add(request.FileExtension);
        WinRT.Interop.InitializeWithWindow.Initialize(openPicker, windowHandle);
        var selected = await openPicker.PickSingleFileAsync();
        cancellationToken.ThrowIfCancellationRequested();
        return selected?.Path;
    }

    private async Task<IReadOnlyList<string>> PickImageAttachmentsAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
        };
        foreach (var extension in new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp" })
        {
            picker.FileTypeFilter.Add(extension);
        }
        var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);
        var selected = await picker.PickMultipleFilesAsync();
        cancellationToken.ThrowIfCancellationRequested();
        return selected
            .Select(file => file.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();
    }

    private Task PublishMaintenanceEventAsync(WebEvent webEvent) =>
        RunOnUiThreadAsync(() => PostWebEvent(webEvent));

    private Task PublishCloudEventAsync(WebEvent webEvent) =>
        RunOnUiThreadAsync(() => PostWebEvent(webEvent));

    private Task PublishWindowsAutomationEventAsync(WebEvent webEvent) =>
        RunOnUiThreadAsync(() => PostWebEvent(webEvent));

    private Task PublishImageAttachmentEventAsync(WebEvent webEvent) =>
        RunOnUiThreadAsync(() => PostWebEvent(webEvent));

    private Task<bool> RequestExtensionApprovalAsync(
        ExtensionApprovalRequest request,
        CancellationToken cancellationToken)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            return _contentDialogQueue.EnqueueAsync(
                token => ShowExtensionApprovalAsync(request, token),
                shutdownResult: false,
                cancellationToken);
        }

        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    completion.TrySetResult(
                        await _contentDialogQueue.EnqueueAsync(
                            token => ShowExtensionApprovalAsync(request, token),
                            shutdownResult: false,
                            cancellationToken));
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            }))
        {
            completion.TrySetException(
                new InvalidOperationException("The AgentDesk UI dispatcher is unavailable."));
        }
        return completion.Task;
    }

    private async Task<bool> ShowExtensionApprovalAsync(
        ExtensionApprovalRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_isClosing)
        {
            return false;
        }

        var scope = Text(request.Scope switch
        {
            ExtensionScope.Mcp => "ExtensionScopeMcp",
            ExtensionScope.Skills => "ExtensionScopeSkills",
            ExtensionScope.Hooks => "ExtensionScopeHooks",
            ExtensionScope.Plugins => "ExtensionScopePlugins",
            ExtensionScope.Marketplace => "ExtensionScopeMarketplace",
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        });
        var action = Text(ExtensionApprovalPresentation.ActionResourceKey(request.Action));
        var content = new TextBlock
        {
            Text = FormatText(
                "ExtensionApprovalBody",
                scope,
                action,
                request.Target),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 480,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = Text("ExtensionApprovalTitle"),
            Content = content,
            PrimaryButtonText = Text("ExtensionApprovalAllowOnce"),
            CloseButtonText = Text("ExtensionApprovalDeny"),
            DefaultButton = ContentDialogButton.Close,
        };
        using var registration = cancellationToken.Register(() =>
            _ = DispatcherQueue.TryEnqueue(dialog.Hide));
        var result = await dialog.ShowAsync();
        cancellationToken.ThrowIfCancellationRequested();
        return result is ContentDialogResult.Primary;
    }

    private Task<bool> RequestWindowsAutomationApprovalAsync(
        WindowsAutomationApprovalRequest request,
        CancellationToken cancellationToken)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            return _contentDialogQueue.EnqueueAsync(
                token => ShowWindowsAutomationApprovalAsync(request, token),
                shutdownResult: false,
                cancellationToken);
        }

        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    completion.TrySetResult(
                        await _contentDialogQueue.EnqueueAsync(
                            token => ShowWindowsAutomationApprovalAsync(request, token),
                            shutdownResult: false,
                            cancellationToken));
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            }))
        {
            completion.TrySetException(
                new InvalidOperationException("The AgentDesk UI dispatcher is unavailable."));
        }
        return completion.Task;
    }

    private async Task<bool> ShowWindowsAutomationApprovalAsync(
        WindowsAutomationApprovalRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_isClosing)
        {
            return false;
        }

        var action = Text(request.Action switch
        {
            WindowsAutomationAction.FocusWindow => "WindowsAutomationActionFocus",
            WindowsAutomationAction.Invoke => "WindowsAutomationActionInvoke",
            WindowsAutomationAction.SetValue => "WindowsAutomationActionSetValue",
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        });
        var content = new TextBlock
        {
            Text = FormatText(
                "WindowsAutomationApprovalBody",
                action,
                request.ProcessId,
                request.Target,
                request.ValueCharacters),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 480,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = Text("WindowsAutomationApprovalTitle"),
            Content = content,
            PrimaryButtonText = Text("WindowsAutomationAllowOnce"),
            CloseButtonText = Text("WindowsAutomationDeny"),
            DefaultButton = ContentDialogButton.Close,
        };
        using var registration = cancellationToken.Register(() =>
            _ = DispatcherQueue.TryEnqueue(dialog.Hide));
        var result = await dialog.ShowAsync();
        cancellationToken.ThrowIfCancellationRequested();
        return result is ContentDialogResult.Primary;
    }

    private async Task PrepareForRestoreAsync(CancellationToken cancellationToken)
    {
        await CloseWebViewsForRestoreAsync(cancellationToken).ConfigureAwait(false);
        await _commandDispatcher
            .DisposeImageAttachmentsAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private Task CloseWebViewsForRestoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return RunOnUiThreadAsync(() =>
        {
            if (_webViewsClosedForMaintenance)
            {
                return;
            }

            _webViewsClosedForMaintenance = true;
            WorkbenchWebView.Visibility = Visibility.Collapsed;
            InspectorWebView.Visibility = Visibility.Collapsed;
            foreach (var coreWebView in new[]
                     {
                         WorkbenchWebView.CoreWebView2,
                         InspectorWebView.CoreWebView2,
                     })
            {
                if (coreWebView is not null)
                {
                    coreWebView.WebMessageReceived -= CoreWebView2_WebMessageReceived;
                }
            }
            WorkbenchWebView.Close();
            InspectorWebView.Close();
            SetWaitingStatus(Text("RestoringBackup"));
        });
    }

    private async Task RestartApplicationAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            await RunOnUiThreadAsync(() => ShowError(Text("ApplicationRestartFailed")));
            throw new InvalidOperationException("The AgentDesk executable path is unavailable.");
        }

        var information = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
        };
        foreach (var argument in BuildRestartArguments(_launchOptions))
        {
            information.ArgumentList.Add(argument);
        }

        if (Process.Start(information) is null)
        {
            await RunOnUiThreadAsync(() => ShowError(Text("ApplicationRestartFailed")));
            throw new InvalidOperationException("AgentDesk could not start a replacement process.");
        }
        await RequestApplicationExitAsync(cancellationToken);
    }

    private Task RequestApplicationExitAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return RunOnUiThreadAsync(Close);
    }

    private Task RunOnUiThreadAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (DispatcherQueue.HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    action();
                    completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            }))
        {
            completion.TrySetException(
                new InvalidOperationException("The AgentDesk UI dispatcher is unavailable."));
        }
        return completion.Task;
    }

    private Task SetModalStateAsync(bool isOpen, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InspectorWebView.Visibility = isOpen ? Visibility.Collapsed : Visibility.Visible;
        InspectorWebView.IsHitTestVisible = !isOpen;
        InspectorWebView.IsTabStop = !isOpen;
        return Task.CompletedTask;
    }

    private void HostController_EventProduced(object? sender, WebEvent webEvent)
    {
        if (_isClosing)
        {
            return;
        }

        if (webEvent is UiPreferencesChangedWebEvent preferences)
        {
            _ = ApplyBackgroundUpdatePreferenceAsync(
                preferences.Preferences.BackgroundUpdateChecksEnabled);
        }
        if (webEvent is WorkspaceSelectedWebEvent or SessionActiveChangedWebEvent)
        {
            _ = ClearNativeImageAttachmentsAndPostAsync(webEvent);
            return;
        }

        _ = DispatcherQueue.TryEnqueue(() => PostWebEvent(webEvent));
    }

    private async Task ClearNativeImageAttachmentsAndPostAsync(WebEvent webEvent)
    {
        try
        {
            await _commandDispatcher
                .ClearImageAttachmentsAsync(_windowShutdown.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_windowShutdown.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_isClosing)
        {
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"AgentDesk image attachment cleanup failed: {exception}");
        }

        _ = DispatcherQueue.TryEnqueue(() => PostWebEvent(webEvent));
    }

    private async Task ApplyBackgroundUpdatePreferenceAsync(bool enabled)
    {
        try
        {
            await _backgroundUpdateMonitor
                .SetEnabledAsync(enabled, _windowShutdown.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_windowShutdown.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_isClosing)
        {
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"AgentDesk background update preference failed: {exception}");
        }
    }

    private void PostWebEvent(WebEvent webEvent)
    {
        if (_isClosing || _webViewsClosedForMaintenance)
        {
            return;
        }

        var json = WebMessageProtocol.SerializeEvent(webEvent);
        WorkbenchWebView.CoreWebView2?.PostWebMessageAsJson(json);
        if (WebEventRoutingPolicy.ProjectForInspector(webEvent) is { } inspectorEvent)
        {
            var inspectorJson = ReferenceEquals(inspectorEvent, webEvent)
                ? json
                : WebMessageProtocol.SerializeEvent(inspectorEvent);
            InspectorWebView.CoreWebView2?.PostWebMessageAsJson(inspectorJson);
        }
        ApplyNativeStatus(webEvent);
    }

    private void ApplyNativeStatus(WebEvent webEvent)
    {
        switch (webEvent)
        {
            case WorkspaceSelectedWebEvent workspace:
                WorkspaceStatusText.Text = workspace.Path;
                ToolTipService.SetToolTip(WorkspaceStatusText, workspace.Path);
                break;
            case EngineStatusWebEvent { Status: "ready" }:
                SetReadyStatus(Text("EngineConnected"));
                EngineStatusText.Text = Text("EngineConnected");
                break;
            case EngineStatusWebEvent { Status: "starting" } status:
                SetWaitingStatus(status.Message ?? Text("EngineStarting"));
                EngineStatusText.Text = Text("EngineStarting");
                break;
            case EngineStatusWebEvent { Status: "running" } status:
                SetWaitingStatus(status.Message ?? Text("TaskRunning"));
                EngineStatusText.Text = Text("TaskRunning");
                break;
            case EngineStatusWebEvent { Status: "idle" } status:
                SetWaitingStatus(status.Message ?? Text("WaitingForTask"));
                EngineStatusText.Text = Text("EngineDisconnected");
                break;
            case EngineStatusWebEvent { Status: "error" or "stopped" } status:
                TitleStatusDot.Fill = ResourceBrush("AgentDeskErrorBrush");
                StatusDot.Fill = ResourceBrush("AgentDeskErrorBrush");
                TitleStatusText.Text = status.Message ?? Text("EngineUnavailable");
                EngineStatusText.Text = status.Status == "stopped"
                    ? Text("EngineStopped")
                    : Text("EngineError");
                break;
        }
    }

    private void ShowLoading()
    {
        _loadedSurfaces.Clear();
        WorkbenchWebView.Visibility = Visibility.Collapsed;
        InspectorWebView.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        LoadingPanel.Visibility = Visibility.Visible;
        SetWaitingStatus(Text("InterfaceLoading"));
    }

    private void ShowError(string message)
    {
        ErrorMessageText.Text = message;
        WorkbenchWebView.Visibility = Visibility.Collapsed;
        InspectorWebView.Visibility = Visibility.Collapsed;
        LoadingPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Visible;
        TitleStatusDot.Fill = ResourceBrush("AgentDeskErrorBrush");
        StatusDot.Fill = ResourceBrush("AgentDeskErrorBrush");
        TitleStatusText.Text = Text("ActionRequired");
        EngineStatusText.Text = Text("InterfaceUnavailable");
    }

    private void SetWaitingStatus(string message)
    {
        TitleStatusDot.Fill = ResourceBrush("AgentDeskWaitingBrush");
        StatusDot.Fill = ResourceBrush("AgentDeskWaitingBrush");
        TitleStatusText.Text = message;
        EngineStatusText.Text = Text("EngineDisconnected");
    }

    private void SetReadyStatus(string message)
    {
        TitleStatusDot.Fill = ResourceBrush("AgentDeskReadyBrush");
        StatusDot.Fill = ResourceBrush("AgentDeskReadyBrush");
        TitleStatusText.Text = message;
        EngineStatusText.Text = Text("LocalWorkbench");
    }

    private static Brush ResourceBrush(string key)
    {
        return (Brush)Application.Current.Resources[key];
    }

    private async void MainWindow_Closing(
        AppWindow sender,
        AppWindowClosingEventArgs args)
    {
        if (_shutdownCoordinator.TryConsumeCloseAuthorization())
        {
            return;
        }

        args.Cancel = true;
        PrepareForShutdown();
        SetWaitingStatus(Text("EngineClosingSafely"));
        EngineStatusText.Text = Text("EngineClosing");

        var result = await _shutdownCoordinator.RequestShutdownAsync();
        if (_localResourcesDisposed)
        {
            return;
        }

        if (!result.Succeeded)
        {
            if (_shutdownCoordinator.ShouldReportFailure(result))
            {
                ShowShutdownError();
            }
            return;
        }

        await Task.Yield();
        if (_localResourcesDisposed)
        {
            return;
        }

        if (!_shutdownCoordinator.TryAuthorizeClose())
        {
            return;
        }

        Close();
    }

    private void PrepareForShutdown()
    {
        if (_isClosing)
        {
            return;
        }

        _isClosing = true;
        _notificationActivationCoordinator.Dispose();
        _windowsAutomationCoordinator.Dispose();
        _contentDialogQueue.Close();
        try
        {
            _windowShutdown.Cancel();
        }
        catch (AggregateException)
        {
            // Controller disposal must still run if a command cancellation callback fails.
        }

        _hostController.EventProduced -= HostController_EventProduced;
        if (!_webViewsClosedForMaintenance)
        {
            foreach (var coreWebView in new[]
                     {
                         WorkbenchWebView.CoreWebView2,
                         InspectorWebView.CoreWebView2,
                     })
            {
                if (coreWebView is not null)
                {
                    coreWebView.WebMessageReceived -= CoreWebView2_WebMessageReceived;
                }
            }
        }
    }

    private void ShowShutdownError()
    {
        ErrorMessageText.Text = Text("EngineShutdownFailedBody");
        WorkbenchWebView.Visibility = Visibility.Collapsed;
        InspectorWebView.Visibility = Visibility.Collapsed;
        LoadingPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Visible;
        ReloadButton.IsEnabled = false;
        TitleStatusDot.Fill = ResourceBrush("AgentDeskErrorBrush");
        StatusDot.Fill = ResourceBrush("AgentDeskErrorBrush");
        TitleStatusText.Text = Text("ShutdownFailedRetry");
        EngineStatusText.Text = Text("EngineCleanupFailed");
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        if (_localResourcesDisposed)
        {
            return;
        }

        _localResourcesDisposed = true;
        AppWindow.Closing -= MainWindow_Closing;
        Closed -= MainWindow_Closed;
        _windowShutdown.Dispose();
        _minimumWindowSize.Dispose();
        if (!_webViewsClosedForMaintenance)
        {
            WorkbenchWebView.Close();
            InspectorWebView.Close();
        }
        _backgroundUpdateMonitor.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _commandDispatcher.DisposeImageAttachmentsAsync().GetAwaiter().GetResult();
        _updateCoordinator.Dispose();
        _maintenanceCoordinator.Dispose();
        _cloudCoordinator.Dispose();
        _windowsAutomationCoordinator.Dispose();
        _notificationActivationCoordinator.Dispose();
    }

    private string Text(string key) => _strings.Get(key);

    private string FormatText(string key, params object[] values) =>
        string.Format(CultureInfo.CurrentCulture, Text(key), values);
}
