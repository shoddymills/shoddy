// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using Xunit;

// THE WHOLE ASSEMBLY RUNS SEQUENTIALLY, and that is a correctness
// requirement here rather than a preference about speed.
//
// These tests do not merely compute. They run whole Shoddy programs through
// the real engine and spawn real child processes, and several of them reach
// for state that is PROCESS-WIDE and cannot be held twice at once:
//
//   --allow-net      a global switch NetTests sets and clears around a
//                    loopback round trip
//   loopback ports   bound for real by the socket tests
//   the file root    FileRootTests changes what paths resolve against
//   the console, Engine.PendingSink, the debug adapter - one per process
//
// xUnit runs test COLLECTIONS in parallel, and a class with no [Collection]
// attribute is a collection of one. Most classes here name "golden" and were
// serial with each other; the eight that had forgotten the attribute ran
// alongside them. So the global network switch was being toggled underneath
// tests that assumed it was steady, and the symptom was not a failed
// assertion but a DEAD TEST HOST - the run aborting partway with
// "TCPSECURE: handle is a listener" and everything after it never running.
//
// TWO SEPARATE FAULTS PRODUCED THE SAME SYMPTOM, which is what made this
// expensive to find. The other one was a pipe deadlock in the process
// helpers, fixed in ProcessRun.cs. Fixing that alone let the suite pass
// once and then crash on the next run, because the parallel socket races
// remained; fixing the races alone did nothing while the deadlock stood.
// Both are needed, and neither on its own is evidence the job is done.
//
// Disabling parallelisation for the assembly removes the class of problem
// rather than the instance of it. The cost is small - the bulk of the suite
// was already serial inside "golden" - and a release gate buys determinism
// cheaply at that price.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
