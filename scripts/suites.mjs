// THE SUITE ROSTER - THE ONLY COPY.
//
// build.ps1 and build.sh read these lists through node, and
// scripts/verify-suites.js proves every tst/*.shoddy is either run by a
// list here or excluded below with its reason written down. Suites keep
// falling out of hand-kept lists silently - nine at once historically,
// then seedterminaltest again in 2026 - and a suite that stops being run
// proves nothing while looking like it does.
//
// This file used to live inside the gate harness (scripts/gate/steps.mjs).
// The harness is gone - CI is the proof now - but the roster's reason to
// exist never depended on it, so it moved here whole.

// The numerics and conformance suites that run before the machine sweep,
// in order. Why each one is a suite at all:
//
//   libtest             the golden library suite
//   builtinsurfacetest  not a machine suite: it compares the RUNTIME's
//                       builtin dispatch against the seeded dictionary,
//                       and fails if a builtin is reachable that a stated
//                       rule says must not be, or if a future seed quietly
//                       claims a name the builtin seed's work list expects
//                       to be free
//   fin                 arithmetic that produces a plausible wrong answer
//                       rather than an error, so its known answers run in
//                       CI beside the golden files rather than by hand
//   eng, lin            the same case: a numerics library that is subtly
//                       wrong still returns numbers. eng is known answers;
//                       lin adds residuals against the defining identities
//                       (P A - L U, A v - lambda v), the only way to test
//                       a factorisation whose parts are not unique
//   alg                 a symbolic answer that is subtly wrong is still a
//                       well-formed expression, so it tests by property:
//                       integrals differentiated back, factorisations
//                       expanded, partial fractions recombined - and it is
//                       the only place the alg/eng bridge is exercised
//   bool                arithmetic standing in for bits: known answers
//                       worked by hand plus the identities - De Morgan
//                       both ways, Gray codes one bit apart, a minimised
//                       cover rebuilt and compared
//   sparse, mip         sparse kernels are checked against matrix.shoddy
//                       computing the same thing densely; mip's knapsack
//                       is chosen so the integer answer is neither the
//                       relaxation's nor the relaxation rounded
//   geo                 fixtures worked by the spherical law of cosines
//                       where the machine uses haversine - a different
//                       formula reaching the same number
//   julian              the calendar, against public epoch facts
//   ephemeris           the sky, against the eclipse and the oppositions
//   clock               the timing capability, by consistency not moment
//   sinq                ordering fixtures a wrong-but-plausible sort
//                       disagrees with, and one section big enough to
//                       catch a stack-bound word
export const CORE_SUITES = [
    'libtest', 'builtinsurfacetest', 'fin', 'eng', 'lin', 'alg', 'bool',
    'sparse', 'mip', 'geo', 'julian', 'ephemeris', 'clock', 'sinq',
];

// One suite per machine that has one, by name rather than by glob: a suite
// that stops being listed is a suite that stops running.
export const MACHINE_SUITES = [
    'csvtest', 'cuttletest', 'htmltest', 'jsontest', 'nettest', 'neuraltest',
    'randomtest', 'reckonertest', 'regextest', 'seedtest', 'seedbooltest',
    'seedbuiltintest',
    'seedengtest', 'seedbuzzertest', 'seedfintest', 'seedhttpstest',
    'seedisamtest', 'seedmiptest', 'seedneuraltest', 'seednettest',
    'seedreciotest', 'seedregextest', 'seedsimplextest', 'seedsparsetest',
    'seedterminaltest',
    'seedvt100test', 'shakertest', 'xmltest', 'seedgeotest',
    'seedjuliantest', 'seedephemeristest', 'seedsinqtest',
];

// tst/ files run by a dedicated line in build.ps1/build.sh rather than by
// a loop, each because it needs something the loop cannot give it:
// net-demo needs --allow-net, sinq-demo asserts nothing (it proves the
// docs' worked example still compiles), and the isam pair is ordered -
// isamdump reopens what isamtest leaves behind and takes DELETE.
export const DEDICATED = ['net-demo', 'sinq-demo', 'isamtest', 'isamdump'];

// tst/ files deliberately run by nothing, each with the reason. A name
// here is a decision on the record; a name in neither place fails
// verify-suites.js. The "needs a real display" entries are what
// scripts/display.ps1 runs - by that exact wording, so a fourth windowed
// suite added here is picked up there automatically.
export const TST_EXCLUSIONS = {
    'seedscribblertest': 'needs a real display: GLFW has no platform on hosted runners; run scripts/display.ps1',
    'seedturtletest': 'needs a real display: GLFW has no platform on hosted runners; run scripts/display.ps1',
    'seedplottertest': 'needs a real display: GLFW has no platform on hosted runners; run scripts/display.ps1',
    'buzzer-demo': 'interactive demo, not a suite',
    'html-demo': 'interactive demo, not a suite',
    'https-demo': 'interactive demo, not a suite',
    'plotter-demo': 'interactive demo, not a suite',
    'scribbler-demo': 'interactive demo, not a suite',
    'shaker-demo': 'interactive demo, not a suite',
    'turtle-demo': 'interactive demo, not a suite',
    'examples': 'a grab-bag of worked defs, not a suite',
    'simplex': 'a worked demo of the simplex machine - prints, asserts nothing',
    'gradebook': "the guide's interactive worked example; Input blocks an unattended run",
    'isamfixture': 'shared record shape Included by the isam suites; no Main',
};
