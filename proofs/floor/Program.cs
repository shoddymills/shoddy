// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

// The floor consumer does one real thing, so the output being asserted
// is the output of a working program, not of an empty shell.

using System.Reflection;
using Shoddy.Hosting;

ShoddyHost host = ShoddyHost.Load(Assembly.Load("Shoddy.Machines.Pure-core"));
ShoddyValue s = host.Word("SampleOf").Call(ShoddyValue.Str("F"), ShoddyValue.Num(90));
Console.WriteLine(host.Word("Grade").Call(s).AsStr());
return 0;
