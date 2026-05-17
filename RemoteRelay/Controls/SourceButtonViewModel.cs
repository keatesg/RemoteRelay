using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using ReactiveUI;

namespace RemoteRelay.Controls;

public class SourceButtonViewModel : ViewModelBase
{
    private readonly BehaviorSubject<SourceState> _state = new(SourceState.Inactive);
    private readonly bool _hasIdentityColour;
    private readonly bool _isOutputDestination;
    private SolidColorBrush _backgroundColor = new(Colors.DarkSlateBlue);
    private SolidColorBrush _foregroundColor = new(Colors.White);
    private Color _linkedColor = Colors.Gray;
    private bool _isEnabled = true;

    public SourceButtonViewModel(string sourceName, Color? linkedColor = null, bool isOutputDestination = false)
    {
        SourceName = sourceName;
        _hasIdentityColour = linkedColor.HasValue;
        _isOutputDestination = isOutputDestination;
        _linkedColor = linkedColor ?? Colors.Gray;

        var canExecute = _state
           .Select(state => state != SourceState.Selected)
           .CombineLatest(this.WhenAnyValue(vm => vm.IsEnabled), (stateAvailable, enabled) => stateAvailable && enabled)
           .DistinctUntilChanged();

        SelectSource =
           ReactiveCommand
              .CreateFromObservable(
                 () => Observable.Return(Unit.Default),
                 canExecute);

        _ = _state
           .CombineLatest(this.WhenAnyValue(vm => vm.IsEnabled), (state, enabled) => (state, enabled))
           .Select(tuple =>
           {
               if (!tuple.enabled)
               {
                   var disabledBg = LookupBrushColor("SourceDisabledBrush", Colors.DarkSlateGray);
                   var disabledFg = LookupBrushColor("SourceDisabledForegroundBrush", Colors.DimGray);
                   return (Background: disabledBg, Foreground: disabledFg);
               }

               if (tuple.state == SourceState.Inactive && !_hasIdentityColour)
               {
                   if (_isOutputDestination)
                   {
                       // Output button with nothing routed: distinctly idle.
                       var idleBg = LookupBrushColor("OutputNeutralBrush", Color.FromRgb(0x32, 0x37, 0x44));
                       var idleFg = LookupBrushColor("OutputNeutralForegroundBrush", Color.FromRgb(0x8A, 0x8E, 0x96));
                       return (Background: idleBg, Foreground: idleFg);
                   }

                   // Action button (Cancel, Confirm, Unroute) – keep the normal neutral look.
                   var actionBg = LookupBrushColor("ActionButtonBrush", Colors.DarkSlateBlue);
                   return (Background: actionBg, Foreground: SourcePaletteBuilder.GetReadableForeground(actionBg));
               }

               var bg = tuple.state switch
               {
                   SourceState.Inactive => SourcePaletteBuilder.Dim(_linkedColor),
                   SourceState.Selected => LookupBrushColor("SourceSelectedFlashBrush", Colors.Red),
                   SourceState.Active => _linkedColor,
                   SourceState.Linked => _linkedColor,
                   _ => Colors.Pink
               };
               return (Background: bg, Foreground: SourcePaletteBuilder.GetReadableForeground(bg));
           })
           .ObserveOn(RxApp.MainThreadScheduler)
           .Subscribe(x =>
           {
               BackgroundColor = new SolidColorBrush(x.Background);
               ForegroundColor = new SolidColorBrush(x.Foreground);
           });
    }

    public string SourceName { get; }

    public ReactiveCommand<Unit, Unit> SelectSource { get; }

    public IObservable<Unit> Clicked => SelectSource;

    public SolidColorBrush BackgroundColor
    {
        get => _backgroundColor;
        set => this.RaiseAndSetIfChanged(ref _backgroundColor, value);
    }

    public SolidColorBrush ForegroundColor
    {
        get => _foregroundColor;
        set => this.RaiseAndSetIfChanged(ref _foregroundColor, value);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => this.RaiseAndSetIfChanged(ref _isEnabled, value);
    }

    private IDisposable? _flashDisposable;

    public void SetState(SourceState state, Color? linkedColor = null)
    {
        if (linkedColor.HasValue)
            _linkedColor = linkedColor.Value;

        // Stop any existing flash animation
        _flashDisposable?.Dispose();
        _flashDisposable = null;

        Dispatcher.UIThread.Invoke(() => _state.OnNext(state));
    }

    /// <summary>
    /// Start a flash animation that alternates between the supplied colour and a neutral off-colour.
    /// Used when FlashOnSelect is enabled and an input is selected.
    /// </summary>
    public void StartFlashAnimation(Color flashColor)
    {
        // Stop any existing flash
        _flashDisposable?.Dispose();

        const int flashCount = 6;
        const int flashIntervalMs = 150;

        var flashOff = LookupBrushColor("SourceFlashOffBrush", Colors.DarkGray);
        var selectedSettle = LookupBrushColor("SourceSelectedFlashBrush", Colors.Red);

        _flashDisposable = Observable
           .Interval(TimeSpan.FromMilliseconds(flashIntervalMs))
           .Take(flashCount)
           .ObserveOn(RxApp.MainThreadScheduler)
           .Subscribe(
              tick =>
              {
                  var isOn = tick % 2 == 0;
                  var background = isOn ? flashColor : flashOff;
                  BackgroundColor = new SolidColorBrush(background);
                  ForegroundColor = new SolidColorBrush(SourcePaletteBuilder.GetReadableForeground(background));
              },
              () =>
              {
                  BackgroundColor = new SolidColorBrush(selectedSettle);
                  ForegroundColor = new SolidColorBrush(SourcePaletteBuilder.GetReadableForeground(selectedSettle));
              });
    }

    private static Color LookupBrushColor(string key, Color fallback)
    {
        var app = Application.Current;
        if (app is null)
        {
            return fallback;
        }

        if (app.Resources.TryGetResource(key, app.ActualThemeVariant, out var resource) &&
            resource is ISolidColorBrush brush)
        {
            return brush.Color;
        }

        return fallback;
    }
}

public enum SourceState
{
    Inactive,
    Selected,
    Active,
    Linked
}
