/**
 * Where the concurrent-session limit actually comes from.
 *
 * It used to be a plain number in Settings that the user could type anything
 * into — which is wrong in both directions. Typing 50 on a plan with 5 seats did
 * not buy 50 sessions; the browser refuses them, so the app promised a limit it
 * could not honour and the failure surfaced as a launch error instead of a
 * disabled input. And a user on a 200-seat plan was silently capped at the
 * factory default of 5 with nothing explaining why.
 *
 * The plan is therefore the ceiling, and the setting becomes a *preference*
 * underneath it: a user with 200 seats may still want to cap at 8 because their
 * machine has 16 GB of RAM. Lowering is a legitimate choice, raising past the
 * entitlement is not.
 *
 * One deliberate exception: when the plan is unknown — no key yet, license
 * server unreachable, an enterprise plan with a negotiated seat count, or a plan
 * name this build has never heard of — the cap is *not* guessed. Blocking a
 * paying user's launches because a network call failed would be a far worse bug
 * than allowing one session too many, so an unknown plan falls back to the
 * user's own preference.
 */

/** Fallback when the plan is unknown and the user has no preference stored. */
export const FALLBACK_SESSION_LIMIT = 5;

/** Hard ceiling on the *preference* input, so a typo cannot become a fork bomb. */
export const MAX_SESSION_LIMIT = 500;

export interface SessionLimit {
  /** The number actually enforced at launch time. */
  limit: number;
  /** Seats the plan grants, or null when unknown/unbounded. */
  planSeats: number | null;
  /** The user's stored preference, clamped to something sane. */
  preference: number;
  /** True when the plan — not the preference — is what binds. */
  cappedByPlan: boolean;
  /** Why this number, in words, for the UI and for launch errors. */
  reason: string;
}

/**
 * Resolve the effective limit.
 *
 * @param preference        `settings.maxConcurrentSessions`
 * @param planSeats         seats from the license (null = unknown/unbounded)
 */
export function resolveSessionLimit(
  preference: number | undefined,
  planSeats: number | null | undefined,
): SessionLimit {
  const pref = clampPreference(preference);
  const seats = typeof planSeats === 'number' && planSeats > 0 ? Math.floor(planSeats) : null;

  if (seats === null) {
    return {
      limit: pref,
      planSeats: null,
      preference: pref,
      cappedByPlan: false,
      reason: `your own limit of ${pref} (plan seat count unknown)`,
    };
  }

  if (pref <= seats) {
    return {
      limit: pref,
      planSeats: seats,
      preference: pref,
      cappedByPlan: false,
      reason: `your own limit of ${pref} (your plan allows ${seats})`,
    };
  }

  return {
    limit: seats,
    planSeats: seats,
    preference: pref,
    cappedByPlan: true,
    reason: `your plan's ${seats} seat${seats === 1 ? '' : 's'}`,
  };
}

/** Keep the stored preference in range without silently rewriting the user's file. */
export function clampPreference(value: number | undefined): number {
  if (typeof value !== 'number' || !Number.isFinite(value)) return FALLBACK_SESSION_LIMIT;
  const n = Math.floor(value);
  if (n < 1) return 1;
  if (n > MAX_SESSION_LIMIT) return MAX_SESSION_LIMIT;
  return n;
}

/** Upper bound for the Settings input: the plan, or the hard ceiling if unknown. */
export function preferenceMax(planSeats: number | null | undefined): number {
  return typeof planSeats === 'number' && planSeats > 0
    ? Math.min(Math.floor(planSeats), MAX_SESSION_LIMIT)
    : MAX_SESSION_LIMIT;
}
