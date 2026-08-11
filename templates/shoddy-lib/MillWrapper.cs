// The scaffold's wrapper: one class exposing the mill's words as
// ordinary methods. Rename and extend — the pattern is the point:
// ShoddyHost.Load once, ShoddyValue in, .NET values out.

using System.Reflection;
using Shoddy.Hosting;

namespace ShoddyLib;

public static class MillWrapper
{
    // The woven core's assembly is named Shoddy.Machines.<Core-stem> —
    // for a mill whose core is widget-core.shoddy:
    //
    // static readonly ShoddyHost Host =
    //     ShoddyHost.Load(Assembly.Load("Shoddy.Machines.Widget-core"));
    //
    // public static double Score(double n) =>
    //     Host.Word("WidgetScore").Call(ShoddyValue.Num(n)).AsNum();
}
