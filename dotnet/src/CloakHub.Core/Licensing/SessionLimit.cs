namespace CloakHub.Core.Licensing;

/// <summary>
/// Where the concurrent-session limit actually comes from.
/// <para>
/// It used to be a plain number in Settings the user could type anything into —
/// wrong in both directions. Typing 50 on a plan with 5 seats did not buy 50
/// sessions; the browser refuses them, so the app promised a limit it could not
/// honour and the failure surfaced as a launch error instead of a disabled input.
/// And a user on a 200-seat plan was silently capped at the factory default with
/// nothing explaining why.
/// </para>
/// <para>
/// The plan is therefore the ceiling, and the setting becomes a <i>preference</i>
/// underneath it: a user with 200 seats may still want to cap at 8 because their
/// machine has 16 GB of RAM. Lowering is a legitimate choice; raising past the
/// entitlement is not.
/// </para>
/// <para>
/// One deliberate exception: when the plan is unknown — no key yet, license
/// server unreachable, an enterprise plan with a negotiated seat count, or a plan
/// name this build has never heard of — the cap is <b>not</b> guessed. Blocking a
/// paying user's launches because a network call failed is a far worse bug than
/// allowing one session too many, so an unknown plan falls back to the user's own
/// preference.
/// </para>
/// </summary>
public static class SessionLimit
{
    /// <summary>Fallback when the plan is unknown and no preference is stored.</summary>
    public const int Fallback = 5;

    /// <summary>Hard ceiling on the <i>preference</i> input, so a typo cannot become a fork bomb.</summary>
    public const int MaxPreference = 500;

    public sealed record Resolution
    {
        /// <summary>The number actually enforced at launch time.</summary>
        public int Limit { get; init; }

        /// <summary>Seats the plan grants, or null when unknown/unbounded.</summary>
        public int? PlanSeats { get; init; }

        /// <summary>The user's stored preference, clamped to something sane.</summary>
        public int Preference { get; init; }

        /// <summary>True when the plan — not the preference — is what binds.</summary>
        public bool CappedByPlan { get; init; }

        /// <summary>Why this number, in words, for the UI and for launch errors.</summary>
        public string Reason { get; init; } = "";
    }

    /// <summary>Clamp a stored preference into a usable range.</summary>
    public static int ClampPreference(int? preference)
    {
        if (preference is null or <= 0) return Fallback;
        return Math.Min(preference.Value, MaxPreference);
    }

    /// <summary>Resolve the effective limit.</summary>
    public static Resolution Resolve(int? preference, int? planSeats)
    {
        var pref = ClampPreference(preference);
        var seats = planSeats is > 0 ? planSeats.Value : (int?)null;

        if (seats is null)
            return new Resolution
            {
                Limit = pref,
                PlanSeats = null,
                Preference = pref,
                CappedByPlan = false,
                Reason = $"your own limit of {pref} (plan seat count unknown)",
            };

        if (pref <= seats.Value)
            return new Resolution
            {
                Limit = pref,
                PlanSeats = seats,
                Preference = pref,
                CappedByPlan = false,
                Reason = $"your own limit of {pref} (plan allows {seats})",
            };

        return new Resolution
        {
            Limit = seats.Value,
            PlanSeats = seats,
            Preference = pref,
            CappedByPlan = true,
            Reason = $"your plan's {seats} concurrent session{(seats == 1 ? "" : "s")}",
        };
    }

    /// <summary>
    /// Seats granted by a named plan.
    /// <para>
    /// <c>null</c> means "unknown or unbounded", which <see cref="Resolve"/>
    /// treats as "fall back to the preference" rather than guessing. Enterprise is
    /// deliberately null: the seat count is negotiated, so any number here would
    /// be a fabrication.
    /// </para>
    /// </summary>
    public static int? SeatsForPlan(string? plan) => (plan ?? "").Trim().ToLowerInvariant() switch
    {
        "free" => 1,
        "solo" => 5,
        "team" => 20,
        "scale" => 200,
        "enterprise" => null,
        _ => null,
    };

    /// <summary>
    /// Merge a newly-discovered seat count into a stored preference.
    /// <para>
    /// Only ever <i>raises</i> the preference. A user who deliberately lowered
    /// their limit to protect a small machine must not have that overwritten the
    /// next time activation succeeds — which the first version of this logic did,
    /// by simply assigning the seat count.
    /// </para>
    /// </summary>
    public static int MergeAfterActivation(int? storedPreference, int? planSeats)
    {
        var pref = ClampPreference(storedPreference);
        if (planSeats is not > 0) return pref;
        return Math.Max(pref, Math.Min(planSeats.Value, MaxPreference));
    }
}
