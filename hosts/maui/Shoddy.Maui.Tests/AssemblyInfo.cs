// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

// Sequential on purpose: the suite shares process-global seams — the
// scribbler registry, the buzzer registry, and the working directory
// the engine resolves file words against.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
