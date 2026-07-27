using CloakHub.App.ViewModels;
using CloakHub.Core.Cookies;

namespace CloakHub.App.Tests;

/// <summary>
/// The cookie tab's view model.
/// <para>
/// These run against a real temporary Chromium store rather than a mocked service.
/// The panel's job is to report what is actually in the database — the count, the
/// domains, whether a session survived the write — and a fake that returned whatever
/// was asked of it would confirm the panel's expectations instead of the store's
/// behaviour, which is the half that has been wrong before.
/// </para>
/// </summary>
public sealed class CookiePanelViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cookiepanel-" + Guid.NewGuid().ToString("N"));

    private bool _running;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* best effort */ }
    }

    private CookiePanelViewModel Subject(out ToastHost toasts)
    {
        toasts = new ToastHost();

        var service = new CookieService(
            id => Path.Combine(_root, id),
            _ => _running);

        return new CookiePanelViewModel("p1", service, _ => _running, toasts);
    }

    private CookiePanelViewModel Subject() => Subject(out _);

    private const string TwoCookies = """
        [{"name":"SID","value":"a","domain":".google.com","path":"/","secure":true,"httpOnly":true,"expirationDate":1900000000},
         {"name":"c_user","value":"b","domain":".facebook.com","path":"/","secure":true,"expirationDate":1900000000}]
        """;

    // ---------------------------------------------------------------------
    // Import gating. Every one of these is a state in which the button's
    // enabled-ness is the only thing telling the user what is missing.

    [Fact]
    public void Import_is_blocked_until_something_is_pasted()
    {
        var vm = Subject();

        Assert.False(vm.CanImport);

        vm.Paste = TwoCookies;

        Assert.True(vm.CanImport);
    }

    [Fact]
    public void A_bare_header_blocks_import_until_a_domain_is_given()
    {
        var vm = Subject();

        vm.Paste = "sessionid=abc; csrftoken=xyz";

        Assert.True(vm.NeedsDomain);
        Assert.False(vm.CanImport);

        vm.Domain = "instagram.com";

        Assert.True(vm.CanImport);
    }

    [Fact]
    public void Typing_a_domain_re_enables_the_import_button()
    {
        // The regression this project was created for, and the reason it is asserted
        // through the command rather than through CanImport.
        //
        // CanImport is computed on every read, so a test that reads it directly
        // passes even with the notification missing -- verified by deleting the fix,
        // at which point the property-based assertion above still passed while the
        // real button stayed greyed out. What the UI actually binds to is the
        // command, which caches its enabled state until CanExecuteChanged tells it
        // otherwise. Asserting on that is what makes this test able to fail.
        var vm = Subject();
        vm.Paste = "sessionid=abc; csrftoken=xyz";

        Assert.False(vm.ImportCommand.CanExecute(null));

        var raised = 0;
        vm.ImportCommand.CanExecuteChanged += (_, _) => raised++;

        vm.Domain = "instagram.com";

        Assert.True(vm.ImportCommand.CanExecute(null));
        Assert.True(raised > 0, "the domain field must re-raise CanExecuteChanged");
    }

    [Fact]
    public void Typing_into_the_paste_box_re_enables_the_import_button()
    {
        // The same failure mode on the other input, checked the same way.
        var vm = Subject();

        Assert.False(vm.ImportCommand.CanExecute(null));

        var raised = 0;
        vm.ImportCommand.CanExecuteChanged += (_, _) => raised++;

        vm.Paste = TwoCookies;

        Assert.True(vm.ImportCommand.CanExecute(null));
        Assert.True(raised > 0, "the paste box must re-raise CanExecuteChanged");
    }

    [Fact]
    public void Clearing_the_domain_blocks_a_header_import_again()
    {
        var vm = Subject();
        vm.Paste = "sessionid=abc";
        vm.Domain = "instagram.com";

        vm.Domain = "   ";

        // Whitespace is not a domain. Accepting it would put the cookies on a host
        // named " ", which no site will ever match.
        Assert.False(vm.CanImport);
    }

    [Fact]
    public void A_domain_is_not_demanded_for_formats_that_carry_one()
    {
        var vm = Subject();

        vm.Paste = TwoCookies;

        Assert.False(vm.NeedsDomain);
        Assert.True(vm.CanImport);
    }

    [Fact]
    public void Rubbish_blocks_import_and_says_so()
    {
        var vm = Subject();

        vm.Paste = "this is not a cookie export";

        Assert.True(vm.PasteIsError);
        Assert.False(vm.CanImport);
        Assert.True(vm.HasPasteSummary);
    }

    [Fact]
    public void A_running_browser_blocks_import()
    {
        _running = true;
        var vm = Subject();

        vm.Paste = TwoCookies;

        Assert.True(vm.IsRunning);
        Assert.False(vm.CanImport);
    }

    // ---------------------------------------------------------------------
    // The pre-import readout.

    [Fact]
    public void The_readout_names_the_format_the_count_and_the_services()
    {
        var vm = Subject();

        vm.Paste = TwoCookies;

        Assert.Contains("2 cookies", vm.PasteSummary);
        Assert.Contains("JSON", vm.PasteSummary);
        Assert.Contains("Google", vm.PasteSummary);
        Assert.Contains("Facebook", vm.PasteSummary);
        Assert.False(vm.PasteIsError);
    }

    [Fact]
    public void Emptying_the_box_clears_the_readout()
    {
        var vm = Subject();
        vm.Paste = TwoCookies;

        vm.Paste = "";

        Assert.False(vm.HasPasteSummary);
        Assert.False(vm.PasteIsError);
        Assert.False(vm.NeedsDomain);
    }

    // ---------------------------------------------------------------------
    // Import, and what the panel shows afterwards.

    [Fact]
    public void Importing_stores_the_cookies_and_reports_the_store()
    {
        var vm = Subject();
        vm.Paste = TwoCookies;

        vm.ImportCommand.Execute(null);

        Assert.Equal(2, vm.Count);
        Assert.True(vm.HasCookies);
        Assert.Equal("2 cookies stored", vm.CountLabel);
        Assert.Contains("google.com", vm.StoredDomains);
        Assert.Contains("facebook.com", vm.StoredDomains);
        Assert.Contains("Google", vm.StoredServices);
    }

    [Fact]
    public void A_successful_import_empties_the_paste_box()
    {
        var vm = Subject();
        vm.Paste = TwoCookies;

        vm.ImportCommand.Execute(null);

        // So a second press cannot silently re-import the same payload, and the box
        // is ready for the next account.
        Assert.Equal("", vm.Paste);
    }

    [Fact]
    public void A_failed_import_keeps_the_paste_box_intact()
    {
        // The payload may have been fetched with some effort. Discarding it on
        // failure would mean going back for it again.
        var vm = Subject();
        vm.Paste = TwoCookies;
        _running = true;

        vm.ImportCommand.Execute(null);

        Assert.Equal(TwoCookies, vm.Paste);
        Assert.Equal(0, vm.Count);
    }

    [Fact]
    public void A_failed_import_reports_the_reason()
    {
        var vm = Subject(out var toasts);
        vm.Paste = TwoCookies;
        _running = true;

        vm.ImportCommand.Execute(null);

        var toast = Assert.Single(toasts.Items);
        Assert.Equal(ToastKind.Error, toast.Kind);
        Assert.Contains("Close this profile's browser", toast.Message);
    }

    [Fact]
    public void Importing_a_header_uses_the_domain_that_was_typed()
    {
        var vm = Subject();
        vm.Paste = "sessionid=abc; csrftoken=xyz";
        vm.Domain = "instagram.com";

        vm.ImportCommand.Execute(null);

        Assert.Equal(2, vm.Count);
        Assert.Contains("instagram.com", vm.StoredDomains);
    }

    [Fact]
    public void A_second_import_merges_by_default()
    {
        var vm = Subject();
        vm.Paste = TwoCookies;
        vm.ImportCommand.Execute(null);

        vm.Paste = """[{"name":"li_at","value":"c","domain":".linkedin.com","path":"/","secure":true,"expirationDate":1900000000}]""";
        vm.ImportCommand.Execute(null);

        Assert.Equal(3, vm.Count);
        Assert.Contains("linkedin.com", vm.StoredDomains);
        Assert.Contains("google.com", vm.StoredDomains);
    }

    [Fact]
    public void Replace_drops_what_was_there_before()
    {
        var vm = Subject();
        vm.Paste = TwoCookies;
        vm.ImportCommand.Execute(null);

        vm.Replace = true;
        vm.Paste = """[{"name":"li_at","value":"c","domain":".linkedin.com","path":"/","secure":true,"expirationDate":1900000000}]""";
        vm.ImportCommand.Execute(null);

        Assert.Equal(1, vm.Count);
        Assert.DoesNotContain("google.com", vm.StoredDomains);
    }

    // ---------------------------------------------------------------------
    // Clear.

    [Fact]
    public void Clear_empties_the_store()
    {
        var vm = Subject();
        vm.Paste = TwoCookies;
        vm.ImportCommand.Execute(null);

        vm.ClearCommand.Execute(null);

        Assert.Equal(0, vm.Count);
        Assert.False(vm.HasCookies);
        Assert.Empty(vm.StoredDomains);
        Assert.Equal("No cookies stored", vm.CountLabel);
    }

    [Fact]
    public void Clear_is_refused_while_the_browser_is_running()
    {
        var vm = Subject(out var toasts);
        vm.Paste = TwoCookies;
        vm.ImportCommand.Execute(null);

        _running = true;
        vm.ClearCommand.Execute(null);

        Assert.Equal(2, vm.Count);
        Assert.Contains(toasts.Items, t => t.Kind == ToastKind.Error);
    }

    // ---------------------------------------------------------------------
    // Export.

    [Fact]
    public async Task Export_writes_the_chosen_file()
    {
        var vm = Subject();
        vm.Paste = TwoCookies;
        vm.ImportCommand.Execute(null);

        var target = Path.Combine(_root, "out.json");
        vm.SavePicker = _ => Task.FromResult<string?>(target);

        vm.ExportJsonCommand.Execute(null);
        await WaitFor(() => File.Exists(target));

        var written = CookieParser.Parse(File.ReadAllText(target));
        Assert.Equal(2, written.Count);
    }

    [Fact]
    public async Task Cancelling_the_save_dialog_writes_nothing()
    {
        var vm = Subject(out var toasts);
        vm.Paste = TwoCookies;
        vm.ImportCommand.Execute(null);
        toasts.Items.Clear();

        vm.SavePicker = _ => Task.FromResult<string?>(null);
        vm.ExportJsonCommand.Execute(null);
        await Task.Delay(50);

        // No file, and no "exported 0 cookies" claiming otherwise.
        Assert.Empty(toasts.Items);
    }

    // ---------------------------------------------------------------------
    // A profile that has never been launched.

    [Fact]
    public void A_profile_with_no_store_yet_reads_as_empty_rather_than_failing()
    {
        // The normal state of a freshly created profile: the directory does not
        // exist at all. Surfacing that as an error would put a red toast on every
        // new profile the user opens.
        var vm = Subject(out var toasts);

        Assert.Equal(0, vm.Count);
        Assert.False(vm.HasCookies);
        Assert.Empty(toasts.Items);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        // AsyncRelayCommand.Execute is async void, so the work is not finished when
        // it returns. Polling rather than a fixed delay keeps the test fast when the
        // operation completes immediately, which it usually does.
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }
}
