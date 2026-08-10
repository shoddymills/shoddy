// The scaffold's whole program: load the mill's machines, call a word.
// Replace with your own — ShoddyHost.Load takes the woven machine
// assemblies (referenced by ShoddyWeave, loadable by name), Word() finds
// a word case-insensitively, Call() marshals values in and out.

using System.Reflection;
using Shoddy.Hosting;

// The woven core's assembly is named Shoddy.Machines.<Core-stem> — for a
// mill whose core is widget-core.shoddy:
//   var host = ShoddyHost.Load(Assembly.Load("Shoddy.Machines.Widget-core"));
//   Console.WriteLine(host.Word("SomeWord").Call(ShoddyValue.Num(21)).AsNum());

Console.WriteLine("scaffolded — point ShoddyHost.Load at your mill's woven core");
return 0;
