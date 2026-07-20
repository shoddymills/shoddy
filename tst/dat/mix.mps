* Copyright (c) 2026 Stephen Vincent Foster. All rights reserved.
*
* This file is part of the Shoddy Language project.
* Licensed under the Shoddy Language License 1.0.0 (PolyForm Noncommercial
* License 1.0.0 with Additional Use Grant). See the LICENSE file in the
* project root for full terms.
*
* The supplement mix, as MPS - the same problem tst/simplex.shoddy
* states inline:
*
*   Minimize  COST = 5 X + 7 Y
*   s.t.      VITA: 2 X + 1 Y >= 8     (Vitamin A)
*             VITC: 1 X + 2 Y >= 10    (Vitamin C)
*             X, Y >= 0
*
* Optimum: X = 2, Y = 4, COST = 38; shadow prices VITA 1, VITC 3.
* Try it: bin\mill run tst\simplex-mps.shoddy tst/dat/mix.mps
NAME MIX
ROWS
 N COST
 G VITA
 G VITC
COLUMNS
 X COST 5 VITA 2
 X VITC 1
 Y COST 7 VITA 1
 Y VITC 2
RHS
 RHS VITA 8 VITC 10
ENDATA
