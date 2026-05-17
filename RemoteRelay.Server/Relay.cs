using RemoteRelay.Common;
using RemoteRelay.Server.Drivers;

namespace RemoteRelay.Server;

public class Source
{
   private readonly Dictionary<string, RelayConfig> _outputs;
   private IRelayDriver? _driver;

   public Source(string sourceName)
   {
      _outputs = new Dictionary<string, RelayConfig>();
      _sourceName = sourceName;
   }

   public string _sourceName { get; set; }

   public void AddOutput(IRelayDriver driver, RelayConfig config)
   {
      _driver = driver;
      driver.RegisterChannel(config);
      _outputs[config.OutputName] = config;
   }

   public void EnableOutput(string output = "")
   {
      Console.WriteLine($"EnableOutput called with output: '{output}'");
      Console.WriteLine($"Available outputs: {string.Join(", ", _outputs.Keys)}");

      if (_driver == null)
      {
         Console.WriteLine("EnableOutput: no driver registered for this source");
         return;
      }

      if (!_outputs.ContainsKey(output))
      {
         Console.WriteLine($"Output '{output}' NOT found in relay outputs");
         return;
      }

      foreach (var entry in _outputs)
      {
         var config = entry.Value;
         var energized = entry.Key == output;
         Console.WriteLine($"Setting channel {config.RelayPin} {(energized ? "ON" : "OFF")} for output '{entry.Key}'");
         _driver.SetRelay(config.RelayPin, energized);
      }
   }

   public void DisableOutput()
   {
      Console.WriteLine($"DisableOutput called for source '{_sourceName}'");
      if (_driver == null) return;

      foreach (var entry in _outputs)
      {
         var config = entry.Value;
         Console.WriteLine($"Setting channel {config.RelayPin} OFF for output '{entry.Key}'");
         _driver.SetRelay(config.RelayPin, false);
      }
   }

   public string GetCurrentRoute()
   {
      if (_driver == null) return string.Empty;

      foreach (var entry in _outputs)
      {
         if (_driver.GetRelay(entry.Value.RelayPin))
         {
            return entry.Key;
         }
      }
      return string.Empty;
   }
}
