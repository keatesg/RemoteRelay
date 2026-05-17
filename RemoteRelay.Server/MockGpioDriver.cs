using System;
using System.Collections.Generic;
using System.Device.Gpio;
using System.Threading;

namespace RemoteRelay.Server;

public class MockGpioDriver : GpioDriver
{
    private readonly Dictionary<int, PinValue> _pinStates = new();
    private static MockGpioDriver? _instance;

    protected override int PinCount => 40;

    public MockGpioDriver()
    {
        _instance = this;
        // Initialize with Studio 1 active (pin 26 = Low, others = High for ActiveLow config)
        _pinStates[26] = PinValue.Low;  // Studio 1 - Active
        _pinStates[20] = PinValue.High; // Studio 2 - Inactive  
        _pinStates[21] = PinValue.High; // Automation - Inactive
        _pinStates[12] = PinValue.Low;  // Inactive relay - Active
    }

    public static void UpdatePinState(int pinNumber, PinValue value)
    {
        if (_instance != null)
        {
            Console.WriteLine($"MockGpioDriver.UpdatePinState: Updating pin {pinNumber} to {value}");
            _instance._pinStates[pinNumber] = value;
        }
    }

    protected int ConvertPinNumberToLogicalNumberingScheme(int pinNumber)
    {
        Console.WriteLine($"Converting pin {pinNumber} to logical numbering scheme");
        return pinNumber;
    }

    protected override void OpenPin(int pinNumber)
    {
        Console.WriteLine($"Opening pin {pinNumber}");
    }

    protected override void ClosePin(int pinNumber)
    {
        Console.WriteLine($"Closing pin {pinNumber}");
    }

    protected override void SetPinMode(int pinNumber, PinMode mode)
    {
        Console.WriteLine($"Setting pin {pinNumber} to mode {mode}");
    }

    protected override PinMode GetPinMode(int pinNumber)
    {
        Console.WriteLine($"Getting pin mode for pin {pinNumber}");
        return PinMode.Input;
    }

    protected override bool IsPinModeSupported(int pinNumber, PinMode mode)
    {
        Console.WriteLine($"Checking if pin {pinNumber} supports mode {mode}");
        return true;
    }

    protected override void Write(int pinNumber, PinValue value)
    {
        Console.WriteLine($"MockGpioDriver.Write: Writing value {value} to pin {pinNumber}");
        _pinStates[pinNumber] = value;
    }

    protected override WaitForEventResult WaitForEvent(int pinNumber, PinEventTypes eventTypes,
        CancellationToken cancellationToken)
    {
        Console.WriteLine($"Waiting for event {eventTypes} on pin {pinNumber}");
        return new WaitForEventResult();
    }

    protected override PinValue Read(int pinNumber)
    {
        var value = _pinStates.ContainsKey(pinNumber) ? _pinStates[pinNumber] : PinValue.High; // Default to High for inactive relays
        Console.WriteLine($"Reading value from pin {pinNumber}: {value}");
        return value;
    }

    protected override void AddCallbackForPinValueChangedEvent(int pinNumber, PinEventTypes eventTypes,
        PinChangeEventHandler callback)
    {
        Console.WriteLine($"Adding callback for pin {pinNumber} on event {eventTypes}");
    }

    protected override void RemoveCallbackForPinValueChangedEvent(int pinNumber, PinChangeEventHandler callback)
    {
        Console.WriteLine($"Removing callback for pin {pinNumber}");
    }

    protected WaitForEventResult WaitForEvent(int pinNumber, PinEventTypes eventTypes, TimeSpan timeout)
    {
        Console.WriteLine($"Waiting for event {eventTypes} on pin {pinNumber} with timeout {timeout}");
        return new WaitForEventResult();
    }

    protected override void Dispose(bool disposing)
    {
        Console.WriteLine("Disposing MockGpioDriver");
        base.Dispose(disposing);
    }
}