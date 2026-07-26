/**
 * Session-limit resolution tests.
 *
 * The reported problem was that the limit was a number the user typed with
 * nothing behind it: "лимит сессий должен идти из лицензии, а не из вольно
 * набранного числа". So the assertions here are less about arithmetic and more
 * about who wins, and they pin down three cases that are easy to get wrong:
 *
 *   - plan below preference  → plan wins (the entitlement is real)
 *   - preference below plan  → preference wins (running 200 browsers on a laptop
 *                              is the user's call to refuse, not ours to force)
 *   - plan unknown           → preference wins, NOT a guessed cap (a network
 *                              blip must never block a paying user's launches)
 *
 * The third is the one worth guarding hardest: "unknown" is not "free tier".
 */

import { describe, expect, it } from 'vitest';
import {
  FALLBACK_SESSION_LIMIT,
  MAX_SESSION_LIMIT,
  clampPreference,
  preferenceMax,
  resolveSessionLimit,
} from '../src/shared/session-limit';

describe('resolveSessionLimit — the plan is the ceiling', () => {
  it('caps at the plan when the user asked for more', () => {
    const r = resolveSessionLimit(50, 5);
    expect(r.limit).toBe(5);
    expect(r.cappedByPlan).toBe(true);
    expect(r.planSeats).toBe(5);
    expect(r.preference).toBe(50);
  });

  it('says the plan is what bound, so the UI does not advise raising Settings', () => {
    // The old error told the user to "raise the limit in Settings", which does
    // nothing when the plan is the constraint — the fix has to be visible here.
    const r = resolveSessionLimit(50, 5);
    expect(r.reason).toMatch(/plan/i);
    expect(r.reason).not.toMatch(/your own limit/i);
  });

  it('pluralises a single seat correctly', () => {
    expect(resolveSessionLimit(10, 1).reason).toMatch(/1 seat\b/);
    expect(resolveSessionLimit(10, 2).reason).toMatch(/2 seats\b/);
  });
});

describe('resolveSessionLimit — a lower preference is a legitimate choice', () => {
  it('honours a preference below the plan instead of forcing the entitlement', () => {
    const r = resolveSessionLimit(3, 200);
    expect(r.limit).toBe(3);
    expect(r.cappedByPlan).toBe(false);
  });

  it('reports both numbers so the user can see the headroom they are not using', () => {
    expect(resolveSessionLimit(3, 200).reason).toMatch(/allows 200/);
  });

  it('treats an exactly-equal preference as not capped by the plan', () => {
    const r = resolveSessionLimit(20, 20);
    expect(r.limit).toBe(20);
    expect(r.cappedByPlan).toBe(false);
  });
});

describe('resolveSessionLimit — unknown plan must not block launches', () => {
  it('falls back to the preference rather than guessing a cap', () => {
    for (const seats of [null, undefined]) {
      const r = resolveSessionLimit(12, seats);
      expect(r.limit).toBe(12);
      expect(r.planSeats).toBeNull();
      expect(r.cappedByPlan).toBe(false);
    }
  });

  it('does NOT collapse an unknown plan to the free-tier single seat', () => {
    // The dangerous failure mode: license server unreachable, so the app decides
    // the user is on free and refuses their second session.
    expect(resolveSessionLimit(12, null).limit).not.toBe(1);
  });

  it('treats a nonsensical seat count as unknown', () => {
    for (const seats of [0, -5, Number.NaN]) {
      expect(resolveSessionLimit(9, seats).planSeats).toBeNull();
      expect(resolveSessionLimit(9, seats).limit).toBe(9);
    }
  });

  it('says the seat count is unknown rather than implying a plan', () => {
    expect(resolveSessionLimit(9, null).reason).toMatch(/unknown/i);
  });
});

describe('clampPreference', () => {
  it('falls back for a missing or non-numeric value', () => {
    for (const v of [undefined, Number.NaN, Number.POSITIVE_INFINITY]) {
      expect(clampPreference(v)).toBe(FALLBACK_SESSION_LIMIT);
    }
  });

  it('never returns below 1 — a limit of 0 would mean "cannot launch anything"', () => {
    expect(clampPreference(0)).toBe(1);
    expect(clampPreference(-10)).toBe(1);
  });

  it('clamps to the hard ceiling', () => {
    expect(clampPreference(10_000)).toBe(MAX_SESSION_LIMIT);
  });

  it('floors fractional input', () => {
    expect(clampPreference(7.9)).toBe(7);
  });
});

describe('preferenceMax', () => {
  it('is the plan when the plan is known', () => {
    expect(preferenceMax(20)).toBe(20);
  });

  it('is the hard ceiling when the plan is unknown', () => {
    expect(preferenceMax(null)).toBe(MAX_SESSION_LIMIT);
    expect(preferenceMax(undefined)).toBe(MAX_SESSION_LIMIT);
  });

  it('never exceeds the hard ceiling, even for an enormous plan', () => {
    expect(preferenceMax(100_000)).toBe(MAX_SESSION_LIMIT);
  });
});
