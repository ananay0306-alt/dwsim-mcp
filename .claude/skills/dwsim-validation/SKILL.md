---
name: dwsim-validation
description: Use after every DWSIM solve, before reporting any result as trustworthy. Catches simulations that converge cleanly but are physically wrong - wrong property package, unflagged thermal runaway, solidification risk, unclosed recycles.
---

# Validating a DWSIM result

A converged simulation is not a correct simulation. DWSIM will return a full
set of clean numbers for a physically impossible design and report no error.
Run every check below before presenting results as trustworthy.

Work in order. Checks 1-3 are cheap and disqualifying; stop if any fails.

## 1. Solver status - disqualifying

`solve` returns an `errors` array. **Non-empty means the numbers are invalid**,
even though values are populated. Never report results from a run with errors.

If a column will not converge, try
`configure_column {solver_method: "Napthali-Sandholm"}` before assuming the
design is infeasible.

## 2. Mass balance closure - disqualifying

Sum inlet mass flows and outlet mass flows for the whole flowsheet.

- Closure better than 0.1% - acceptable
- Closure worse than 1% - something is wrong, usually an unconnected port or a
  truncated recycle

Do it component-wise too. Overall closure can hide a component that is being
created or destroyed by a mis-specified reaction.

## 3. Recycle convergence - disqualifying

If the flowsheet contains a recycle, confirm the tear stream actually
converged rather than hitting an iteration limit. An unconverged tear stream
produces a mass balance that looks closed only because the solver stopped.

## 4. Property package appropriateness - the failure that hides

**This is the check that catches the errors nothing else will.** A cubic
equation of state assumes no strong specific interactions between molecules.
When that assumption is false, the solver still converges - to a wrong answer.

Flag immediately if a cubic EOS (Peng-Robinson, SRK) is applied to a system
containing any of:

- water together with hydrocarbons
- alcohols, glycols, amines, or organic acids
- any strongly polar or hydrogen-bonding species
- known or suspected azeotropes

For those, the liquid phase needs an activity-coefficient model - NRTL,
UNIQUAC, or UNIFAC. A mixed assignment is often correct: an EOS for a
gas-phase reactor, an activity model for the liquid separation section.

**Diagnostic:** ask what the activity coefficients actually are. If they are
far from 1 while the model assumes near-ideal mixing, the result is wrong no
matter how cleanly it converged.

*Reference failure:* Peng-Robinson on a water/heavy-alkane system where the
true activity coefficient was ~100. The dehydration stage removed no water and
the solver reported nothing. Reassigning UNIQUAC to the separation section cut
water carryover from 4.8 to 0.7 mol/s.

## 5. Physical plausibility

The solver has no concept of buildable. Check explicitly:

- **Temperatures.** Are any stream or reactor temperatures outside what the
  equipment or materials tolerate? An adiabatic reactor can run away to a
  temperature that solves fine and cannot be built.
- **Solidification.** Compare every stream temperature against the melting
  points of its components, especially column bottoms. A stream operating a
  degree or two above a solidification point is a plugged column.
- **Vapor fractions** within [0, 1].
- **No negative** flows, compositions, or duties.
- **Pressure profile** monotonic in the expected direction.

*Reference failures:* adiabatic reactor runaway to 1585 C, corrected to a
cooled reactor recovering 54 MW; and column bottoms operating within 1.3 C of
maleic anhydride's 52.8 C melting point.

## 6. Independent cross-check

Verify magnitude against something that does not share the simulator's
assumptions:

- Columns: Fenske-Underwood-Gilliland shortcut for minimum stages and reflux
- Published feed rates or literature yields for the same process
- A second engine (BioSTEAM, hand calculation) on the same separation

Agreement within a few percent is confirmation. A large gap means one of the
two is wrong, and it is worth knowing which before proceeding.

## Reporting

State per check: PASS, FAIL, or NOT APPLICABLE, with the number that justifies
it. If check 4 was not verifiable, say so - an unverified property package is
an open risk, not a pass.

Never describe a result as validated on the strength of convergence alone.
