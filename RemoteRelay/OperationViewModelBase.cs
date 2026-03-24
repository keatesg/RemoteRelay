using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ReactiveUI;
using RemoteRelay.Common;
using RemoteRelay.Controls;

namespace RemoteRelay;

public abstract class OperationViewModelBase : ViewModelBase, IDisposable
{
    private readonly Subject<Unit> _cancelRequests = new();
    private readonly Subject<IObservable<string>> _messageQueue = new();
    private readonly CompositeDisposable _disposables = new();

    private Bitmap? _stationLogo;
    private string _statusMessage = string.Empty;
    private string _currentTime = DateTime.Now.ToString("HH:mm:ss");
    private string _currentDate = DateTime.Now.ToString("dddd d MMMM yyyy");
    private readonly DispatcherTimer _clockTimer;

    private bool? _showIpOverride;

    protected OperationViewModelBase(AppSettings settings, bool? showIpOverride = null, int timeoutSeconds = 3)
    {
        _showIpOverride = showIpOverride;
        Settings = settings;
        TimeoutSeconds = timeoutSeconds;
        ShowIpOnScreen = _showIpOverride ?? settings.ShowIpOnScreen;
        Cancel = new SourceButtonViewModel("Cancel");

        Cancel.Clicked.Subscribe(_ => _cancelRequests.OnNext(Unit.Default)).DisposeWith(_disposables);

        _messageQueue
           .Switch()
           .ObserveOn(RxApp.MainThreadScheduler)
           .Subscribe(message => StatusMessage = message)
           .DisposeWith(_disposables);

        _cancelRequests
           .ObserveOn(RxApp.MainThreadScheduler)
           .Subscribe(_ => HandleCancel())
           .DisposeWith(_disposables);

        SwitcherClient.Instance._stateChanged
           .ObserveOn(RxApp.MainThreadScheduler)
           .Subscribe(status =>
           {
               CurrentStatus = status;
               HandleStatusUpdate(status);
           })
           .DisposeWith(_disposables);

        SwitcherClient.Instance.SettingsUpdates
           .ObserveOn(RxApp.MainThreadScheduler)
           .Subscribe(newSettings =>
           {
               ShowIpOnScreen = _showIpOverride ?? newSettings.ShowIpOnScreen;
           })
           .DisposeWith(_disposables);

        SwitcherClient.Instance._connectionStateChanged
           .Where(isConnected => isConnected)
           .ObserveOn(RxApp.MainThreadScheduler)
           .Subscribe(_ =>
           {
               SwitcherClient.Instance.RequestStatus();
           })
           .DisposeWith(_disposables);

        if (SwitcherClient.Instance.IsConnected)
        {
            SwitcherClient.Instance.RequestStatus();
        }

        // Real-time clock
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) =>
        {
            CurrentTime = DateTime.Now.ToString("HH:mm:ss");
            CurrentDate = DateTime.Now.ToString("dddd d MMMM yyyy");
        };
        _clockTimer.Start();

        _ = LoadStationLogoAsync();
    }

    protected AppSettings Settings { get; }

    protected IReadOnlyDictionary<string, string> CurrentStatus { get; private set; } =
       new Dictionary<string, string>();

    protected SwitcherClient Server => SwitcherClient.Instance;

    protected int TimeoutSeconds { get; }

    protected CompositeDisposable Disposables => _disposables;

    public SourceButtonViewModel Cancel { get; }

    public string StatusMessage
    {
        get => _statusMessage;
        protected set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
    }

    public string CurrentTime
    {
        get => _currentTime;
        private set => this.RaiseAndSetIfChanged(ref _currentTime, value);
    }

    public string CurrentDate
    {
        get => _currentDate;
        private set => this.RaiseAndSetIfChanged(ref _currentDate, value);
    }

    public Bitmap? StationLogo
    {
        get => _stationLogo;
        protected set => this.RaiseAndSetIfChanged(ref _stationLogo, value);
    }

    private bool _showIpOnScreen;
    public bool ShowIpOnScreen
    {
        get => _showIpOnScreen;
        set => this.RaiseAndSetIfChanged(ref _showIpOnScreen, value);
    }

    public bool FlashOnSelect => Settings.FlashOnSelect;

    public string HostIpAddress { get; } = GetLocalIpAddress();

    private static string GetLocalIpAddress()
    {
        try
        {
            // Get all network interfaces that are up and not loopback
            var networkInterface = NetworkInterface.GetAllNetworkInterfaces()
               .Where(ni => ni.OperationalStatus == OperationalStatus.Up &&
                            ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
               .FirstOrDefault();

            if (networkInterface != null)
            {
                var ipProps = networkInterface.GetIPProperties();
                var ipv4Address = ipProps.UnicastAddresses
                   .Where(ua => ua.Address.AddressFamily == AddressFamily.InterNetwork)
                   .Select(ua => ua.Address)
                   .FirstOrDefault();

                if (ipv4Address != null)
                {
                    return ipv4Address.ToString();
                }
            }

            // Fallback: try to get any IPv4 address
            var host = Dns.GetHostEntry(Dns.GetHostName());
            var fallbackAddress = host.AddressList
               .FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork);

            return fallbackAddress?.ToString() ?? "Unknown IP";
        }
        catch
        {
            return "Unknown IP";
        }
    }

    protected IObservable<Unit> CancelStream => _cancelRequests.AsObservable();

    protected void RequestCancel() => _cancelRequests.OnNext(Unit.Default);

    protected void PushStatusMessage(string message) =>
       _messageQueue.OnNext(Observable.Return(message));

    protected void PushStatusMessage(IObservable<string> messageStream) =>
       _messageQueue.OnNext(messageStream);

    protected virtual void HandleCancel()
    {
        HandleStatusUpdate(CurrentStatus);
    }

    protected abstract void HandleStatusUpdate(IReadOnlyDictionary<string, string> newStatus);

    private async Task LoadStationLogoAsync()
    {
        if (SwitcherClient.Instance.ServerUri == null)
        {
            Debug.WriteLine("Server URI is not configured. Cannot load station logo.");
            return;
        }

        try
        {
            var logoUrl = new Uri(SwitcherClient.Instance.ServerUri, "/logo");
            using var httpClient = new HttpClient();
            var response = await httpClient.GetAsync(logoUrl).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                var imageBytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    using var memoryStream = new MemoryStream(imageBytes);
                    StationLogo = new Bitmap(memoryStream);
                });
                Debug.WriteLine($"Successfully loaded station logo from {logoUrl}");
            }
            else
            {
                Debug.WriteLine($"Failed to load station logo. Status code: {response.StatusCode} from {logoUrl}");
                StationLogo = null;
            }
        }
        catch (HttpRequestException ex)
        {
            Debug.WriteLine($"Error loading station logo: {ex.Message}");
            StationLogo = null;
        }
        catch (TaskCanceledException ex)
        {
            Debug.WriteLine($"Error loading station logo (timeout/cancelled): {ex.Message}");
            StationLogo = null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"An unexpected error occurred while loading station logo: {ex.Message}");
            StationLogo = null;
        }
    }

    public void Dispose()
    {
        _clockTimer.Stop();
        _disposables.Dispose();
    }
}
