* Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
* Licensed under the MIT License. See the LICENSE file in the project root.
*
* Bales of fibre, which you cannot buy half of - the small integer
* example, small enough to check by hand.
*
*   Minimize  COST  = 5 A + 4 B + 2 C
*   s.t.      FIBRE = 6 A + 4 B + 3 C >= 10   (tonnes wanted)
*             A, B  >= 0 and whole              (bales)
*             C     binary                      (the one job lot)
*
* A is 6 tonnes for 5, B is 4 tonnes for 4, and C is a one-off job lot
* of 3 tonnes for 2. Per tonne the job lot is much the cheapest, so the
* relaxation takes all of it and a fraction of A: C = 1, A = 7/6,
* costing 7.8333333. Every one of those answers is unbuyable.
*
* Whole bales: A = 1, B = 1, C = 0 - ten tonnes exactly, costing 9.
* Note what that is NOT. It is not the relaxation rounded up (A = 2,
* C = 1, costing 12), and it is not the job lot plus the cheapest
* whole-bale filler (B = 2, C = 1, costing 10). The cheap-looking job
* lot is not worth taking at all, and only searching finds that out.
*
* Try it: dotnet bin/simplex-mps.dll files/bales.mps
NAME BALES
ROWS
 N COST
 G FIBRE
COLUMNS
    MARKER                 'MARKER'                 'INTORG'
 A COST 5 FIBRE 6
 B COST 4 FIBRE 4
    MARKER                 'MARKER'                 'INTEND'
 C COST 2 FIBRE 3
RHS
 RHS FIBRE 10
BOUNDS
 BV BND C
ENDATA
