using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Media;
using RemoteRelay.Common;
using RemoteRelay.Controls;
using ReactiveUI;

namespace RemoteRelay.MultiOutput;

public class MultiOutputViewModel : OperationViewModelBase
{
    private readonly Dictionary<string, SourceButtonViewModel> _inputLookup;
    private readonly Dictionary<string, SourceButtonViewModel> _outputLookup;
    private readonly Dictionary<string, Color> _palette;
    private SourceButtonViewModel? _activeSelection;

    public string FilterStatusMessage { get; }

    public MultiOutputViewModel(AppSettings settings, bool? showIpOverride = null, string filterStatusMessage = "")
        : base(settings, showIpOverride)
    {
        FilterStatusMessage = filterStatusMessage;
        _palette = SourcePaletteBuilder.Build(settings);
        Inputs = settings.Sources
            .Select(source => new SourceButtonViewModel(source, ResolveColour(source)))
            .ToList();
        Outputs = settings.Outputs
            .Select(output => new SourceButtonViewModel(output, isOutputDestination: true))
            .ToList();

        _inputLookup = Inputs.ToDictionary(vm => vm.SourceName);
        _outputLookup = Outputs.ToDictionary(vm => vm.SourceName);

        var cancelRequested = Observable.Merge(
            CancelStream,
            Server._stateChanged.Select(_ => Unit.Default));

        var selectedInputStream = Inputs
            .Select(vm => vm.Clicked.Select(_ => vm))
            .Merge()
            .Select(vm => Observable
                .Return(vm)
                .Merge(
                    Observable
                        .Return((SourceButtonViewModel?)null)
                        .Delay(TimeSpan.FromSeconds(TimeoutSeconds))))
            .Merge(cancelRequested.Select(_ => Observable.Return((SourceButtonViewModel?)null)))
            .Switch()
            .DistinctUntilChanged()
            .Publish()
            .RefCount();

        Disposables.Add(selectedInputStream
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(current =>
            {
                if (_activeSelection != null && _activeSelection != current)
                {
                    RestoreInputState(_activeSelection);
                }

                _activeSelection = current;

                if (current != null)
                {
                    UpdateOutputAvailability(current.SourceName);
                    current.SetState(SourceState.Selected);

                    // Flash the selected input if FlashOnSelect is enabled
                    if (FlashOnSelect)
                    {
                        var flashColor = ResolveColour(current.SourceName);
                        current.StartFlashAnimation(flashColor);
                    }

                    PushStatusMessage(BuildCountdownMessage(current.SourceName));
                }
                else
                {
                    UpdateOutputAvailability(null);
                    foreach (var input in Inputs)
                    {
                        RestoreInputState(input);
                    }
                }
            }));

        Disposables.Add(Server._stateChanged
            .Take(1)
            .Subscribe(status => HandleStatusUpdate(status)));

        if (CurrentStatus.Count > 0)
        {
            HandleStatusUpdate(CurrentStatus);
        }

        var connection = Outputs
        .Select(vm => vm.Clicked.Select(_ => vm))
        .Merge()
        .WithLatestFrom(selectedInputStream, (output, input) => (Output: output, Input: input))
        .Where(tuple => tuple.Input != null)
            .Select(tuple => (Output: tuple.Output, Input: tuple.Input!));

        Disposables.Add(connection.Subscribe(tuple =>
        {
            RequestCancel();

            if (!Server.IsConnected)
            {
                PushStatusMessage("Server connection lost. Please wait for reconnection.");
                return;
            }

            var inputName = tuple.Input.SourceName;
            var outputName = tuple.Output.SourceName;

            PushStatusMessage($"Routing {inputName} to {outputName}...");

            Server.SwitchSource(inputName, outputName);

            PushStatusMessage(
                Observable
                    .Return("No response received from server")
                    .Delay(TimeSpan.FromSeconds(TimeoutSeconds))
                    .StartWith($"Waiting for {outputName} confirmation..."));
        }));

        // Off button – clears the selected input's routing
        OffButton = new SourceButtonViewModel("Unroute");
        OffButton.IsEnabled = false;

        Disposables.Add(selectedInputStream
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(current =>
            {
                OffButton.IsEnabled = current != null;
            }));

        Disposables.Add(OffButton.Clicked
            .WithLatestFrom(selectedInputStream, (_, input) => input)
            .Where(input => input != null)
            .Subscribe(input =>
            {
                RequestCancel();

                if (!Server.IsConnected)
                {
                    PushStatusMessage("Server connection lost. Please wait for reconnection.");
                    return;
                }

                var inputName = input!.SourceName;
                PushStatusMessage($"Clearing route for {inputName}...");
                Server.ClearSource(inputName);

                PushStatusMessage(
                    Observable
                        .Return("No response received from server")
                        .Delay(TimeSpan.FromSeconds(TimeoutSeconds))
                        .StartWith($"Waiting for confirmation..."));
            }));

        Disposables.Add(selectedInputStream
            .Where(vm => vm == null)
            .Subscribe(_ => HandleCancel()));
    }

    public IReadOnlyList<SourceButtonViewModel> Inputs { get; }

    public IReadOnlyList<SourceButtonViewModel> Outputs { get; }

    public SourceButtonViewModel OffButton { get; }

    public bool UseVerticalLayout => Inputs.Count <= 2 && Outputs.Count <= 2;

    protected override void HandleStatusUpdate(IReadOnlyDictionary<string, string> newStatus)
    {
        PushStatusMessage("Updating...");

        if (newStatus.Count == 0)
        {
            foreach (var input in Inputs)
                input.SetState(SourceState.Inactive);

            foreach (var output in Outputs)
            {
                output.SetState(SourceState.Inactive);
                output.IsEnabled = true;
            }

            UpdateOutputAvailability(null);
            PushStatusMessage("No active routes");
            return;
        }

        var outputAssignments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in newStatus.Where(p => !string.IsNullOrWhiteSpace(p.Value)))
        {
            outputAssignments[pair.Value] = pair.Key; // Last writer wins if duplicates exist
        }

        foreach (var input in Inputs)
        {
            if (newStatus.TryGetValue(input.SourceName, out var outputName) && !string.IsNullOrWhiteSpace(outputName))
            {
                var colour = ResolveColour(input.SourceName);
                input.SetState(SourceState.Linked, colour);
            }
            else
            {
                input.SetState(SourceState.Inactive);
            }
        }

        if (_activeSelection != null)
        {
            _activeSelection.SetState(SourceState.Selected);
        }

        foreach (var output in Outputs)
        {
            if (outputAssignments.TryGetValue(output.SourceName, out var sourceName))
            {
                var colour = ResolveColour(sourceName);
                output.SetState(SourceState.Linked, colour);
                output.IsEnabled = true;
            }
            else
            {
                output.SetState(SourceState.Inactive);
                output.IsEnabled = true;
            }
        }

        UpdateOutputAvailability(_activeSelection?.SourceName);

        // Show status for all active bindings
        var activeRoutes = newStatus
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => $"{pair.Key} → {pair.Value}");

        var statusText = string.Join("  |  ", activeRoutes);
        
        if (string.IsNullOrWhiteSpace(statusText))
        {
            statusText = "No active routes";
        }
        
        PushStatusMessage(statusText);
    }

    protected override void HandleCancel()
    {
        _activeSelection = null;
        base.HandleCancel();
    }

    private void RestoreInputState(SourceButtonViewModel input)
    {
        if (CurrentStatus.TryGetValue(input.SourceName, out var output) && !string.IsNullOrWhiteSpace(output))
        {
            input.SetState(SourceState.Linked, ResolveColour(input.SourceName));
        }
        else
        {
            input.SetState(SourceState.Inactive);
        }
    }

    private void UpdateOutputAvailability(string? selectedSource)
    {
        if (string.IsNullOrWhiteSpace(selectedSource))
        {
            foreach (var output in Outputs)
            {
                output.IsEnabled = true;
            }
            return;
        }

        var availableOutputs = Settings.Routes
            .Where(route => string.Equals(route.SourceName, selectedSource, StringComparison.OrdinalIgnoreCase))
            .Select(route => route.OutputName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var output in Outputs)
        {
            output.IsEnabled = availableOutputs.Contains(output.SourceName);
        }
    }

    private Color ResolveColour(string source)
    {
        var colour = SourcePaletteBuilder.Resolve(source, _palette, Settings.ThemePalette);
        _palette[source] = colour;
        return colour;
    }

    private IObservable<string> BuildCountdownMessage(string sourceName)
    {
        return Observable
            .Timer(TimeSpan.Zero, TimeSpan.FromSeconds(1))
            .Take(TimeoutSeconds + 1)
            .Select(x => TimeoutSeconds - x)
            .Select(remaining => $"Select output for {sourceName} – {remaining}s");
    }
}