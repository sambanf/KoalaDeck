using KoalaDeck.Services;

namespace KoalaDeck.Tests;

public class InstalledAppTests
{
    private const string Programs = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\";

    [Fact]
    public void Shortcut_maps_to_the_process_it_starts()
    {
        InstalledApp? app = InstalledAppScanner.ToApp(Programs + "Visual Studio Code.lnk",
            @"C:\Users\me\AppData\Local\Programs\Microsoft VS Code\Code.exe", string.Empty);

        Assert.NotNull(app);
        Assert.Equal("Code", app.Name);
        Assert.Equal("Visual Studio Code", app.Title);
    }

    [Theory]
    [InlineData("--processStart Discord.exe", "Discord")]
    [InlineData("--processStart \"Slack.exe\" --process-start-args \"--startup\"", "Slack")]
    public void Squirrel_update_exe_maps_to_the_real_app(string arguments, string expected)
    {
        InstalledApp? app = InstalledAppScanner.ToApp(Programs + "Discord Inc\\Discord.lnk",
            @"C:\Users\me\AppData\Local\Discord\Update.exe", arguments);

        Assert.Equal(expected, app?.Name);
    }

    [Fact]
    public void Squirrel_app_uses_the_shortcut_icon_and_normal_apps_their_exe()
    {
        InstalledApp? discord = InstalledAppScanner.ToApp(Programs + "Discord.lnk",
            @"C:\Users\me\AppData\Local\Discord\Update.exe", "--processStart Discord.exe", @"C:\Users\me\AppData\Local\Discord\app.ico");
        InstalledApp? code = InstalledAppScanner.ToApp(Programs + "Visual Studio Code.lnk", @"D:\Apps\Code\Code.exe", string.Empty);

        Assert.Equal(@"C:\Users\me\AppData\Local\Discord\app.ico", discord?.IconPath);
        Assert.Equal(@"D:\Apps\Code\Code.exe", code?.IconPath);
    }

    [Fact]
    public void Windows_tools_are_skipped_except_remote_desktop()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.Null(InstalledAppScanner.ToApp(Programs + "Administrative Tools.lnk", windows + @"\System32\control.exe", "/name Microsoft.AdministrativeTools"));
        Assert.Null(InstalledAppScanner.ToApp(Programs + "Word.lnk", windows + @"\Installer\{90160000-0011-0000-0000-0000000FF1CE}\wordicon.exe", string.Empty));
        Assert.Equal("mstsc", InstalledAppScanner.ToApp(Programs + "Remote Desktop Connection.lnk", windows + @"\System32\mstsc.exe", string.Empty)?.Name);
    }

    [Theory]
    [InlineData("Uninstall Spotify.lnk", @"C:\Program Files\Spotify\unins000.exe")]
    [InlineData("Spotify Setup.lnk", @"C:\Program Files\Spotify\SpotifySetup.exe")]
    [InlineData("Manual.lnk", @"C:\Program Files\App\manual.pdf")]
    [InlineData("Website.lnk", @"https://example.com")]
    [InlineData("Updater.lnk", @"C:\Program Files\Updater\Updater.exe")]
    public void Non_apps_are_skipped(string shortcut, string target)
    {
        Assert.Null(InstalledAppScanner.ToApp(Programs + shortcut, target, string.Empty));
    }

    [Fact]
    public void Squirrel_shortcut_without_process_is_skipped()
    {
        Assert.Null(InstalledAppScanner.ToApp(Programs + "Thing.lnk", @"C:\Users\me\AppData\Local\Thing\Update.exe", "--install ."));
    }
}
