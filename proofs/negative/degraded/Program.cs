// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

// The calculator with every optional capability ungranted: no canvas,
// no buzzer, no clock, no net. It must still calculate — degradation
// is the seam's null behaviour, not a refusal (A3.3a).

using System.Reflection;
using Shoddy.Hosting;

string root = Path.Combine(Path.GetTempPath(), "shoddy-degraded", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
ShoddyHost halifax = ShoddyHost.Load(
    new ShoddyHostOptions { FileRoot = root },
    Assembly.Load("Shoddy.Machines.Halifax-core"));

ShoddyValue st = halifax.Word("HxFresh").Call();
ShoddyValue done = halifax.Word("HxDo").Call(st, ShoddyValue.Str("6 7 *"));
string line = halifax.Word("HxShown").Call(done).AsList()[0].AsStr();
Console.WriteLine(line);
return line.Contains("42") ? 0 : 1;
