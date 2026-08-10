// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

// The F# half of C4.1: the same mill, the same API, no wrapper
// assembly. Construction and destructuring must read naturally — a
// record built by a word, fields read by name, values hand-built from
// F# literals — with nothing reproduced from C#.

open System.Reflection
open Shoddy.Hosting

let check ok what =
    if not ok then failwithf "PROOF FAILED: %s" what
    printfn "ok: %s" what

[<EntryPoint>]
let main _ =
    let host = ShoddyHost.Load(Assembly.Load "Shoddy.Machines.Pure-core")

    let sample = host.Word("SampleOf").Call(ShoddyValue.Str "LIN", ShoddyValue.Num 78.0)
    check (sample.TypeName() = "SAMPLE") "a word called from F# answers a record"
    check (sample.Field("Score").AsNum() = 78.0) "a field reads back by name"
    check (host.Word("Grade").Call(sample).AsStr() = "PASS") "the record round-trips"

    let list = ShoddyValue.ListOf [ ShoddyValue.Num 1.0; ShoddyValue.Num 2.0 ]
    check (list.AsList().Count = 2) "a list builds from an F# list"

    let struct (pops, pushes) = host.Word("Grade").Effect
    check (pops = 1 && pushes = 1) "the declared effect reads as a tuple"

    printfn "ALL PROOFS PASSED"
    0
