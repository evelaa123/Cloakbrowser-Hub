namespace CloakHub.Core.Licensing;

/// <summary>
/// The licence layer the rest of the app talks to.
/// <para>
/// Combines the on-disk key, the server check and the remembered seat count into
/// one object, so no caller has to know that a seat limit comes from a plan name
/// which comes from a network call which may not have happened.
/// </para>
/// </summary>
public sealed class LicenseService : IDisposable
{
    private readonly LicenseStore _store;
    private readonly LicenseClient _client;
    private readonly bool _ownsClient;

    public LicenseService(LicenseStore? store = null, LicenseClient? client = null)
    {
        _store = store ?? new LicenseStore();
        _ownsClient = client is null;
        _client = client ?? new LicenseClient();
    }

    public LicenseStore Store => _store;

    /// <summary>
    /// Seats last reported by the server, or null when never successfully checked.
    /// <para>
    /// Remembered in memory because a launch needs the plan's seat limit but must
    /// not make a network call to get it. A call there would either add seconds to
    /// every launch, or — on failure — force a choice between blocking a paying
    /// user for a network blip and ignoring the limit entirely. Caching the answer
    /// from the last successful validation avoids the choice.
    /// </para>
    /// <para>
    /// Null means "unknown", which <see cref="SessionLimit.Resolve"/> treats as
    /// "fall back to the user's own preference" rather than guessing a cap.
    /// </para>
    /// </summary>
    public int? KnownPlanSeats { get; private set; }

    /// <summary>The last state produced, so the UI can render before the first check finishes.</summary>
    public LicenseState Current { get; private set; } = new();

    /// <summary>Is a key present at all? Cheap — no network.</summary>
    public bool HasKey => _store.Read() is { Length: > 0 };

    /// <summary>
    /// Resolve the full licence state.
    /// <para>
    /// <paramref name="checkSessions"/> is separate from the validation call
    /// because the seat count is the slower of the two and is only interesting once
    /// the key is known good — a panel refresh should not pay for it twice.
    /// </para>
    /// </summary>
    public async Task<LicenseState> RefreshAsync(
        int localSessions = 0,
        bool checkSessions = true,
        CancellationToken cancel = default)
    {
        // Repaired before reading, not after: a UTF-16 file decoded as UTF-8 yields
        // a key full of NULs that the server rejects, so validating first would
        // report a perfectly good key as invalid and the repair would arrive too
        // late to change the answer.
        var repaired = _store.RepairEncoding();
        var fromEnv = _store.KeyFromEnvironment;
        var key = _store.Read();

        if (string.IsNullOrEmpty(key))
        {
            return Publish(new LicenseState
            {
                Tier = LicenseTier.None,
                LocalSessions = localSessions,
                KeyFileRepaired = repaired,
            });
        }

        var basis = new LicenseState
        {
            MaskedKey = LicenseStore.Mask(key),
            LocalSessions = localSessions,
            KeyFileRepaired = repaired,
            FromEnvironment = fromEnv,
        };

        var check = await _client.ValidateAsync(key, cancel).ConfigureAwait(false);

        if (check is null)
        {
            // Unknown, not invalid. The saved key still works offline — the binary
            // reads the same file and does not consult us — so the wording says so
            // rather than implying the user has lost access.
            return Publish(basis with
            {
                Tier = LicenseTier.Unknown,
                CheckedAt = DateTimeOffset.UtcNow,
                Seats = KnownPlanSeats,
                Error = "Could not reach the license server. The saved key will still be used offline.",
            });
        }

        var seats = check.Valid ? SessionLimit.SeatsForPlan(check.Plan) : null;

        // Only remembered for a valid key. An expired one must not lower a limit
        // that a still-running session was started under.
        if (check.Valid) KnownPlanSeats = seats;

        var state = basis with
        {
            Tier = check.Valid
                ? (check.Plan.Equals("free", StringComparison.OrdinalIgnoreCase)
                    ? LicenseTier.Free
                    : LicenseTier.Pro)
                : LicenseTier.None,
            Plan = check.Plan,
            Valid = check.Valid,
            Expires = check.Expires,
            Seats = seats,
            CheckedAt = DateTimeOffset.UtcNow,
        };

        if (!check.Valid)
        {
            // The repair is called out here specifically: this is the exact case
            // where the user was staring at "invalid key" for a key that was fine.
            state = state with
            {
                Error = repaired
                    ? "This license key was stored in the wrong text encoding (UTF-16). The file " +
                      "has been rewritten as UTF-8 — press Re-check."
                    : "This license key is invalid or expired.",
            };
            return Publish(state);
        }

        if (checkSessions)
        {
            state = state with
            {
                ActiveSessions = await _client.ActiveSessionsAsync(key, cancel).ConfigureAwait(false),
            };
        }

        return Publish(state);
    }

    /// <summary>
    /// Save a key and check it in one step.
    /// <para>
    /// The key is written <i>before</i> validation on purpose. An offline user
    /// pasting a key they know is good must end up with it saved — the binary reads
    /// the same file directly and does not need our approval — so making the save
    /// conditional on a network call would deny them the one thing they came to do.
    /// </para>
    /// </summary>
    public async Task<LicenseState> ActivateAsync(
        string key,
        int localSessions = 0,
        CancellationToken cancel = default)
    {
        var normalised = LicenseKeyFile.Normalise(key);
        if (normalised.Length == 0)
        {
            return Publish(Current with
            {
                Error = "That does not look like a license key — the box was empty after trimming.",
            });
        }

        try
        {
            _store.Save(normalised);
        }
        catch (Exception e)
        {
            return Publish(Current with { Error = $"Could not save the key: {e.Message}" });
        }

        return await RefreshAsync(localSessions, checkSessions: true, cancel).ConfigureAwait(false);
    }

    /// <summary>Forget the key, on disk and in memory.</summary>
    public LicenseState Clear(int localSessions = 0)
    {
        _store.Clear();

        // Cleared alongside the key: leaving a remembered seat count behind would
        // let launches keep honouring a plan the user no longer has a key for.
        KnownPlanSeats = null;

        return Publish(new LicenseState { Tier = LicenseTier.None, LocalSessions = localSessions });
    }

    /// <summary>The concurrent-session limit to enforce right now, and why.</summary>
    public SessionLimit.Resolution ResolveLimit(int? preference) =>
        SessionLimit.Resolve(preference, KnownPlanSeats);

    private LicenseState Publish(LicenseState state)
    {
        Current = state;
        return state;
    }

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }
}
