// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

// The C8 console proofs, in one run: Mode N with hand-built values and
// a record round trip; Mode N at engine scale through HxDo; Mode T
// through RunWovenAsync from folder + manifest, source unread; and the
// resource-pairing fail-fast. Prints one line per proof and "ALL
// PROOFS PASSED" at the end; any failure throws and the exit code says
// so.

using System.Reflection;
using Shoddy.Hosting;

static void Check(bool ok, string what)
{
    if (!ok) throw new Exception("PROOF FAILED: " + what);
    Console.WriteLine("ok: " + what);
}

// ---- resource pairing (C2.4a): this build granted `file` to halifax,
// so a Load that supplies no root must die at startup, by name.
try
{
    ShoddyHost.Load(Assembly.Load("Shoddy.Machines.Pure-core"));
    throw new Exception("PROOF FAILED: Load without a FileRoot was allowed");
}
catch (InvalidOperationException e)
{
    Check(e.Message.Contains("halifax") && e.Message.Contains("FileRoot"),
          "granted-but-unsupplied file root fails fast, naming the mill");
}

string root = Path.Combine(Path.GetTempPath(), "shoddy-proofs", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var options = new ShoddyHostOptions { FileRoot = root };

// ---- Mode N, hand-built values and a record round trip (spike steps
// 1–2, closed here).
ShoddyHost pure = ShoddyHost.Load(options, Assembly.Load("Shoddy.Machines.Pure-core"));
ShoddyValue sample = pure.Word("SampleOf").Call(ShoddyValue.Str("ADA"), ShoddyValue.Num(70));
Check(sample.TypeName() == "SAMPLE", "a hand-built call answers a record");
Check(sample.Field("Score").AsNum() == 70, "a field reads back by name");
Check(pure.Word("Grade").Call(sample).AsStr() == "PASS",
      "the record round-trips into the next word");
ShoddyValue scaled = pure.Word("Scale").Call(sample, ShoddyValue.Num(0.5));
Check(scaled.Field("Score").AsNum() == 35 && scaled.Field("Label").AsStr() == "ADA",
      "With-update returns a new record, label intact");

// ---- Mode N at engine scale: the halifax calculator through its own
// transaction surface — HxFresh, HxDo, HxShown — without reading its
// source.
ShoddyHost halifax = ShoddyHost.Load(options, Assembly.Load("Shoddy.Machines.Halifax-core"));
ShoddyValue st = halifax.Word("HxFresh").Call();
ShoddyValue done = halifax.Word("HxDo").Call(st, ShoddyValue.Str("2 3 +"));
IReadOnlyList<ShoddyValue> shown = halifax.Word("HxShown").Call(done).AsList();
Check(shown.Count > 0 && shown[0].AsStr().Contains("5"),
      "HxDo answers 2 3 + with x: 5 at engine scale");
ShoddyValue st2 = halifax.Word("HxAfter").Call(st, done);
ShoddyValue done2 = halifax.Word("HxDo").Call(st2, ShoddyValue.Str("10 *"));
Check(halifax.Word("HxShown").Call(done2).AsList()[0].AsStr().Contains("50"),
      "state threads through HxAfter into the next transaction");

// ---- Mode T: the woven console mill through pipes — folder +
// manifest, source unread, exit code asserted (spike step 4, and A7's
// Mode T proof).
var output = new StringWriter();
int exit = await ShoddyHost.RunWovenAsync(Assembly.Load("halifax"),
    output, new StringReader("2 3 +\nQUIT\n"), Array.Empty<string>());
Check(exit == 0, "the woven halifax runs through pipes and exits 0");
Check(output.ToString().Contains("5"), "its piped transcript answers 2 3 +");

Console.WriteLine("ALL PROOFS PASSED");
return 0;
