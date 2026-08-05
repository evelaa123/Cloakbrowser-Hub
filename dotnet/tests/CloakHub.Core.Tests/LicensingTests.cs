using System.Text;
using CloakHub.Core.Licensing;

namespace CloakHub.Core.Tests;

public class LicenseKeyFileTests
{
    private const string Key = "cb_live_abcdef123456";

    // ---------------------------------------------------------------------
    // The actual reported bug: PowerShell writes UTF-16LE, everything reads UTF-8.
    // ---------------------------------------------------------------------

    [Fact]
    public void Reads_utf16le_with_bom_the_way_powershell_writes_it()
    {
        var bytes = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes(Key)).ToArray();
        Assert.Equal(Key, LicenseKeyFile.Normalise(LicenseKeyFile.Decode(bytes)));
    }

    [Fact]
    public void Reads_utf16be_with_bom()
    {
        var bytes = new byte[] { 0xFE, 0xFF }.Concat(Encoding.BigEndianUnicode.GetBytes(Key)).ToArray();
        Assert.Equal(Key, LicenseKeyFile.Normalise(LicenseKeyFile.Decode(bytes)));
    }

    [Fact]
    public void Reads_utf16le_without_a_bom()
    {
        // `printf '%s' key | iconv -t UTF-16LE` and several editors produce this;
        // StreamReader cannot detect it, so it needs the NUL-offset heuristic.
        var bytes = Encoding.Unicode.GetBytes(Key);
        Assert.Equal(Key, LicenseKeyFile.Normalise(LicenseKeyFile.Decode(bytes)));
    }

    [Fact]
    public void Reads_utf16be_without_a_bom()
    {
        var bytes = Encoding.BigEndianUnicode.GetBytes(Key);
        Assert.Equal(Key, LicenseKeyFile.Normalise(LicenseKeyFile.Decode(bytes)));
    }

    [Fact]
    public void Strips_the_utf8_bom_which_survives_a_plain_trim()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(Key)).ToArray();
        var decoded = LicenseKeyFile.Normalise(LicenseKeyFile.Decode(bytes));
        Assert.Equal(Key, decoded);
        Assert.DoesNotContain('\uFEFF', decoded);
    }

    [Fact]
    public void Reads_plain_utf8()
    {
        Assert.Equal(Key, LicenseKeyFile.Normalise(LicenseKeyFile.Decode(Encoding.UTF8.GetBytes(Key))));
    }

    [Fact]
    public void Empty_file_yields_an_empty_key_rather_than_throwing()
    {
        Assert.Equal("", LicenseKeyFile.Decode([]));
        Assert.Equal("", LicenseKeyFile.Normalise(""));
    }

    // ---------------------------------------------------------------------
    // Normalisation of what people actually put in these files.
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("cb_key\n")]
    [InlineData("cb_key\r\n")]
    [InlineData("  cb_key  ")]
    [InlineData("\"cb_key\"")]
    [InlineData("'cb_key'")]
    [InlineData("CLOAKBROWSER_LICENSE_KEY=cb_key")]
    [InlineData("LICENSE_KEY = cb_key")]
    [InlineData("# my key\ncb_key")]
    [InlineData("\n\n cb_key \n\n")]
    public void Normalises_real_world_shapes(string raw)
    {
        Assert.Equal("cb_key", LicenseKeyFile.Normalise(raw));
    }

    [Fact]
    public void An_unpaired_quote_is_kept()
    {
        // More likely part of a mistyped key than a quoting artefact. Silently
        // removing it would send a different key than the user believes they
        // entered, and the resulting "invalid key" would be unexplainable.
        Assert.Equal("\"cb_key", LicenseKeyFile.Normalise("\"cb_key"));
    }

    [Fact]
    public void First_non_comment_line_wins()
    {
        Assert.Equal("cb_first", LicenseKeyFile.Normalise("# comment\n\ncb_first\ncb_second"));
    }

    [Fact]
    public void Interior_nuls_are_dropped()
    {
        Assert.Equal("cbkey", LicenseKeyFile.Normalise("c\0b\0k\0e\0y"));
    }

    // ---------------------------------------------------------------------
    // Repair detection. The point is fixing the file for the upstream CLI and
    // the binary too, not just for this app.
    // ---------------------------------------------------------------------

    [Fact]
    public void A_utf16_file_is_flagged_for_repair()
    {
        var bytes = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes(Key)).ToArray();
        var (key, needsRepair) = LicenseKeyFile.ReadFile(bytes);
        Assert.Equal(Key, key);
        Assert.True(needsRepair);
    }

    [Fact]
    public void An_already_canonical_file_needs_no_repair()
    {
        var (key, needsRepair) = LicenseKeyFile.ReadFile(LicenseKeyFile.CanonicalBytes(Key));
        Assert.Equal(Key, key);
        Assert.False(needsRepair);
    }

    [Fact]
    public void A_file_with_quotes_is_flagged_even_though_utf8()
    {
        // Other readers do not strip quotes, so the file is still broken for them.
        var (_, needsRepair) = LicenseKeyFile.ReadFile(Encoding.UTF8.GetBytes("\"" + Key + "\""));
        Assert.True(needsRepair);
    }

    [Fact]
    public void An_empty_file_is_not_flagged_for_repair()
    {
        // Nothing to repair, and rewriting would create a file where there was none.
        var (key, needsRepair) = LicenseKeyFile.ReadFile([]);
        Assert.Equal("", key);
        Assert.False(needsRepair);
    }

    [Fact]
    public void Repair_round_trips()
    {
        var broken = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes(Key + "\r\n")).ToArray();
        var (key, _) = LicenseKeyFile.ReadFile(broken);
        var (key2, needsRepair2) = LicenseKeyFile.ReadFile(LicenseKeyFile.CanonicalBytes(key));
        Assert.Equal(Key, key2);
        Assert.False(needsRepair2);
    }
}

public class SessionLimitTests
{
    [Fact]
    public void An_unknown_plan_falls_back_to_the_preference_rather_than_guessing()
    {
        // Blocking a paying user's launches because a network call failed is worse
        // than allowing one session too many.
        var r = SessionLimit.Resolve(preference: 12, planSeats: null);
        Assert.Equal(12, r.Limit);
        Assert.False(r.CappedByPlan);
        Assert.Contains("unknown", r.Reason);
    }

    [Fact]
    public void A_preference_below_the_seat_count_is_honoured()
    {
        // Lowering is legitimate: 200 seats on a 16 GB machine is not usable.
        var r = SessionLimit.Resolve(preference: 8, planSeats: 200);
        Assert.Equal(8, r.Limit);
        Assert.False(r.CappedByPlan);
    }

    [Fact]
    public void A_preference_above_the_seat_count_is_capped()
    {
        var r = SessionLimit.Resolve(preference: 50, planSeats: 5);
        Assert.Equal(5, r.Limit);
        Assert.True(r.CappedByPlan);
        Assert.Contains("plan", r.Reason);
    }

    [Fact]
    public void The_reason_is_singular_for_one_seat()
    {
        var r = SessionLimit.Resolve(preference: 10, planSeats: 1);
        Assert.Contains("1 concurrent session", r.Reason);
        Assert.DoesNotContain("sessions", r.Reason);
    }

    [Theory]
    [InlineData(null, SessionLimit.Fallback)]
    [InlineData(0, SessionLimit.Fallback)]
    [InlineData(-4, SessionLimit.Fallback)]
    [InlineData(7, 7)]
    [InlineData(100000, SessionLimit.MaxPreference)]
    public void Preference_clamping(int? input, int expected)
    {
        Assert.Equal(expected, SessionLimit.ClampPreference(input));
    }

    [Theory]
    [InlineData("free", 1)]
    [InlineData("solo", 5)]
    [InlineData("team", 20)]
    [InlineData("scale", 200)]
    [InlineData("FREE", 1)]
    [InlineData("  team  ", 20)]
    public void Known_plans_map_to_seats(string plan, int expected)
    {
        Assert.Equal(expected, SessionLimit.SeatsForPlan(plan));
    }

    [Theory]
    [InlineData("enterprise")]
    [InlineData("something-new")]
    [InlineData("")]
    [InlineData(null)]
    public void Unknown_or_negotiated_plans_report_null_rather_than_a_fabricated_number(string? plan)
    {
        Assert.Null(SessionLimit.SeatsForPlan(plan));
    }

    // ---------------------------------------------------------------------
    // Activation merge — the regression that overwrote a deliberate choice.
    // ---------------------------------------------------------------------

    [Fact]
    public void Activation_raises_a_default_preference_to_the_seat_count()
    {
        Assert.Equal(20, SessionLimit.MergeAfterActivation(SessionLimit.Fallback, 20));
    }

    [Fact]
    public void Activation_never_lowers_a_deliberately_higher_preference()
    {
        Assert.Equal(30, SessionLimit.MergeAfterActivation(30, 20));
    }

    [Fact]
    public void Activation_leaves_the_preference_alone_when_seats_are_unknown()
    {
        Assert.Equal(8, SessionLimit.MergeAfterActivation(8, null));
    }

    [Fact]
    public void Activation_respects_the_hard_ceiling()
    {
        Assert.Equal(SessionLimit.MaxPreference, SessionLimit.MergeAfterActivation(1, 10_000));
    }
}
