using System.Text.Json;

namespace Huh.TestHarness;

// Usage:
//   Huh.TestHarness list        -> JSON array of scenarios (id, expectError, expectedProcess, description)
//   Huh.TestHarness run <id>    -> raise that dialog (blocks until it is closed or the process is killed)
//   Huh.TestHarness             -> interactive menu
internal static class Program
{
    [STAThread] // shell dialogs (IFileOperation) and TaskDialog want an STA thread
    private static int Main(string[] args)
    {
        if (args is ["list"])
        {
            var rows = Scenarios.All.Select(s => new { s.Id, s.ExpectError, s.ExpectedProcess, s.Description });
            Console.WriteLine(JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        if (args is ["run", var id])
        {
            var s = Scenarios.All.FirstOrDefault(x => x.Id == id);
            if (s is null) { Console.Error.WriteLine($"unknown scenario '{id}'"); return 2; }
            return RunOne(s);
        }

        while (true)
        {
            for (int i = 0; i < Scenarios.All.Count; i++)
                Console.WriteLine($"{i + 1,3}. {Scenarios.All[i].Id,-34} {Scenarios.All[i].Description}");
            Console.Write("Pick a number (blank to quit): ");
            var line = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(line)) return 0;
            if (int.TryParse(line, out var n) && n >= 1 && n <= Scenarios.All.Count) RunOne(Scenarios.All[n - 1]);
        }
    }

    private static int RunOne(Scenario s)
    {
        try { s.Run(); return 0; }
        catch (Exception ex) { Console.Error.WriteLine($"{s.Id} failed: {ex.Message}"); return 1; }
    }
}
