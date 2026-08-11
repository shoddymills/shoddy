// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

// CLS compliance is a design constraint on this surface, not a later
// audit (C4.1): consumable from C#, F# and VB with no wrappers, no shim
// assemblies, and no C#-only feature a consumer must reproduce. The F#
// proof project is the acceptance test; this attribute plus a
// warning-free build is what keeps the surface honest between runs.
[assembly: CLSCompliant(true)]
