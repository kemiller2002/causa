// Owner: integration (T10). The single [JSExport] entry point of the engine (Rehearsal 7 finding KD-007: F# does not
// run the JSExport generator). It relays one Limen message as JSON to the F# engine and returns its answer; it holds
// no state and makes no decision of its own.
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace Causa.Host;

[SupportedOSPlatform("browser")]
public static partial class EngineHost
{
    [JSExport]
    public static string Dispatch(string message) => Causa.App.Program.dispatch(message);
}

internal static class Entry
{
    private static void Main()
    {
    }
}
