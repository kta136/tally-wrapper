using System.IO;
using ShowroomBilling.Contracts;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.Tests.TestSupport;
using ShowroomBilling.Desktop.ViewModels;
using ShowroomBilling.Desktop.ViewModels.Admin;
using ShowroomBilling.Desktop.ViewModels.Settings;

namespace ShowroomBilling.Desktop.Tests.Handover;

public sealed class HandoverKeyAndSettingsTests
{
    private static string ScratchKeyPath() => Path.Combine(TestAppData.Root, "keys", Guid.NewGuid().ToString("N"), HandoverKeyStore.FileName);

    [Theory]
    [InlineData("")]
    [InlineData("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh")]
    [InlineData("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8A")]
    [InlineData("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh+")]
    [InlineData("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh=")]
    [InlineData("AAECAwQFBgcICQoLDA0ODx AREhMUFRYXGBkaGxwdHh")]
    public void Anything_but_43_base64url_characters_is_not_a_key(string text)
    {
        Assert.False(HandoverKeyStore.TryParse(text, out _, out var problem));
        Assert.False(string.IsNullOrWhiteSpace(problem));
    }

    [Fact]
    public void A_pasted_key_with_spaces_round_it_is_a_key()
        => Assert.True(HandoverKeyStore.TryParse($"  {HandoverTestLinks.VectorKey}\r\n", out _, out _));

    [Fact]
    public void The_key_is_kept_protected_for_this_Windows_user_and_can_be_removed()
    {
        var path = ScratchKeyPath();
        var store = new HandoverKeyStore(path);
        Assert.False(store.HasKey);
        Assert.Null(store.Load());

        Assert.Null(store.Save(HandoverTestLinks.VectorKey));

        Assert.Equal(HandoverTestLinks.VectorKeyBytes, store.Load());
        var file = File.ReadAllBytes(path);
        Assert.False(ContainsRun(file, HandoverTestLinks.VectorKeyBytes));
        Assert.DoesNotContain(HandoverTestLinks.VectorKey, System.Text.Encoding.ASCII.GetString(file), StringComparison.Ordinal);

        store.Remove();
        Assert.False(store.HasKey);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void A_damaged_key_file_counts_as_no_key()
    {
        var path = ScratchKeyPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3]);

        Assert.Null(new HandoverKeyStore(path).Load());
    }

    [Fact]
    public void The_key_file_lives_in_this_PCs_own_folder()
    {
        var store = new HandoverKeyStore();
        try
        {
            Assert.Null(store.Save(HandoverTestLinks.VectorKey));
            Assert.True(File.Exists(Path.Combine(AppPaths.LocalDirectory, HandoverKeyStore.FileName)));
            Assert.StartsWith(TestAppData.Root, AppPaths.LocalDirectory, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            store.Remove();
        }
    }

    [Fact]
    public void The_section_shows_the_key_masked_and_changes_it_through_the_dialog()
    {
        var store = new HandoverKeyStore(ScratchKeyPath());
        var section = new HandoverSettingsViewModel(store);
        var shown = 0;
        var hidden = 0;
        section.ShowKeyDialog = () => shown++;
        section.HideKeyDialog = () => hidden++;
        Assert.False(section.HasKey);
        Assert.Equal("No key", section.KeyDisplay);
        Assert.False(section.RemoveCommand.CanExecute(null));

        section.ChangeCommand.Execute(null);
        section.NewKey = "not a key";
        section.SaveKeyCommand.Execute(null);

        Assert.Equal(1, shown);
        Assert.Equal(0, hidden);
        Assert.True(section.HasKeyError);
        Assert.False(store.HasKey);

        section.NewKey = HandoverTestLinks.VectorKey;
        Assert.False(section.HasKeyError);
        section.SaveKeyCommand.Execute(null);

        Assert.Equal(1, hidden);
        Assert.True(section.HasKey);
        Assert.Equal(HandoverSettingsViewModel.MaskedKey, section.KeyDisplay);
        Assert.DoesNotContain(HandoverTestLinks.VectorKey[..6], section.KeyDisplay, StringComparison.Ordinal);
        Assert.Equal(string.Empty, section.NewKey);
        Assert.True(section.RemoveCommand.CanExecute(null));
    }

    [Fact]
    public async Task Remove_asks_first_and_removes_only_on_yes()
    {
        var store = new HandoverKeyStore(ScratchKeyPath());
        Assert.Null(store.Save(HandoverTestLinks.VectorKey));
        var section = new HandoverSettingsViewModel(store);
        ConfirmRequest? asked = null;
        var answer = false;
        section.ConfirmHandler = request =>
        {
            asked = request;
            return Task.FromResult(answer);
        };

        await section.RemoveCommand.ExecuteAsync(null);
        Assert.True(store.HasKey);
        Assert.True(asked!.IsDestructive);

        answer = true;
        await section.RemoveCommand.ExecuteAsync(null);
        Assert.False(store.HasKey);
        Assert.False(section.HasKey);
    }

    [Fact]
    public void Hand_over_is_a_section_of_this_PC_only()
    {
        Assert.Equal(SettingsSectionScope.ThisPc, SettingsSections.ScopeOf(SettingsSectionKey.Handover));
        Assert.Equal("Hand-over", SettingsSections.TitleOf(SettingsSectionKey.Handover));
        var settings = HandoverTestMasters.Settings();
        Assert.Equal(
            [SettingsSectionKey.Server, SettingsSectionKey.Handover, SettingsSectionKey.Advanced],
            settings.Sections.Where(section => SettingsSections.ScopeOf(section) == SettingsSectionScope.ThisPc));
        settings.SelectedSection = SettingsSectionKey.Handover;
        Assert.True(settings.IsHandoverVisible);
        Assert.Equal("This PC only", settings.SelectedSectionChipText);
    }

    [Fact]
    public void The_admin_unlock_says_which_bill_a_hand_over_wants_to_update()
    {
        var admin = new AdminUnlockViewModel(null!, new Services.AdminTokenStore());
        Assert.False(admin.HasHandoverContext);
        Assert.Equal("Unlock", admin.UnlockButtonText);
        Assert.Equal("Close", admin.CloseButtonText);

        admin.HandoverContext = "SR/2026-27/4 · Buyer A is in Tally. Updating it needs the admin passcode.";

        Assert.True(admin.HasHandoverContext);
        Assert.Equal("Unlock and update", admin.UnlockButtonText);
        Assert.Equal("Skip this bill", admin.CloseButtonText);
    }

    private static bool ContainsRun(byte[] haystack, byte[] needle)
    {
        for (var start = 0; start + needle.Length <= haystack.Length; start++)
        {
            if (haystack.AsSpan(start, needle.Length).SequenceEqual(needle))
            {
                return true;
            }
        }

        return false;
    }
}
