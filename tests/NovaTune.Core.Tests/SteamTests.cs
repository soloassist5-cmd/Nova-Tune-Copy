using NovaTune.Core.Hardware;
using NovaTune.Core.Steam;

namespace NovaTune.Core.Tests;

public sealed class VdfParserTests
{
    [Fact]
    public void ParsesNestedSections_QuotedAndEscapedValues()
    {
        const string text = """
            "AppState"
            {
                "appid"     "730"
                "name"      "Counter-Strike 2"
                "path"      "C:\\Program Files (x86)\\Steam"
                "quote"     "say \"hi\""
                "UserConfig"
                {
                    "language"  "russian"
                }
            }
            """;

        var root = VdfParser.Parse(text);
        var state = root["AppState"]!;

        Assert.Equal("730", state.GetString("appid"));
        Assert.Equal(@"C:\Program Files (x86)\Steam", state.GetString("path"));
        Assert.Equal("say \"hi\"", state.GetString("quote"));
        Assert.Equal("russian", state.Path("UserConfig", "language")!.Value);
    }

    [Fact]
    public void KeysAreCaseInsensitive_CommentsAndConditionsIgnored()
    {
        const string text = """
            // комментарий
            Root
            {
                Key value // хвостовой комментарий
                "Cond" "1" [$WIN32]
                Sub [$WINDOWS] { a b }
            }
            """;

        var root = VdfParser.Parse(text);

        Assert.Equal("value", root["root"]!.GetString("KEY"));
        Assert.Equal("1", root["Root"]!.GetString("cond"));
        Assert.Equal("b", root.Path("Root", "Sub", "a")!.Value);
    }

    [Fact]
    public void UnknownEscape_IsKeptLiterally()
    {
        var root = VdfParser.Parse("\"p\" \"D:\\Games\"");
        Assert.Equal(@"D:\Games", root.GetString("p"));
    }

    [Theory]
    [InlineData("\"a\" { \"b\" \"c\"")]
    [InlineData("\"a\" \"b\" }")]
    [InlineData("\"a\" \"unterminated")]
    [InlineData("\"a\"")]
    public void MalformedInput_ThrowsFormatException(string text)
    {
        Assert.ThrowsAny<FormatException>(() => VdfParser.Parse(text));
    }

    [Fact]
    public void ParsesUtf8WithBom()
    {
        var root = VdfParser.Parse("\uFEFF\"users\" { \"1\" { \"PersonaName\" \"Игрок\" } }");
        Assert.Equal("Игрок", root.Path("users", "1", "PersonaName")!.Value);
    }
}

public sealed class SteamScannerTests : IDisposable
{
    private readonly TestDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static string Esc(string path) => path.Replace("\\", "\\\\");

    private string CreateSteam(bool withCs2InSecondLibrary = true)
    {
        var steam = _dir.Combine("Steam");
        var lib2 = _dir.Combine("SteamLibrary");
        Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
        Directory.CreateDirectory(Path.Combine(lib2, "steamapps"));

        _dir.WriteFile("Steam/steamapps/libraryfolders.vdf", $$"""
            "libraryfolders"
            {
                "0" { "path" "{{Esc(steam)}}" "apps" { "570" "1" } }
                "1" { "path" "{{Esc(lib2)}}" "apps" { "730" "1" } }
                "2" { "path" "{{Esc(_dir.Combine("Missing"))}}" }
            }
            """);

        _dir.WriteFile("Steam/steamapps/appmanifest_570.acf", """
            "AppState" { "appid" "570" "name" "Dota 2" "installdir" "dota 2 beta" "StateFlags" "4" "SizeOnDisk" "1000" }
            """);

        if (withCs2InSecondLibrary)
        {
            _dir.WriteFile("SteamLibrary/steamapps/appmanifest_730.acf", """
                "AppState" { "appid" "730" "name" "Counter-Strike 2" "installdir" "Counter-Strike Global Offensive" "StateFlags" "4" "LastUpdated" "1700000000" }
                """);
            _dir.WriteFile("SteamLibrary/steamapps/common/Counter-Strike Global Offensive/game/bin/win64/cs2.exe", "");
            _dir.WriteFile("SteamLibrary/steamapps/common/Counter-Strike Global Offensive/game/csgo/cfg/config.cfg", "x");
        }

        // Два аккаунта: у одного есть конфиги CS2, у другого CS2 не запускалась.
        _dir.WriteFile("Steam/config/loginusers.vdf", """
            "users"
            {
                "76561198000000001" { "AccountName" "first" "PersonaName" "Первый" "MostRecent" "1" }
                "76561198000000002" { "AccountName" "second" "PersonaName" "Second" "MostRecent" "0" }
            }
            """);
        var id1 = (76561198000000001UL - SteamScanner.SteamId64Base).ToString();
        var id2 = (76561198000000002UL - SteamScanner.SteamId64Base).ToString();
        _dir.WriteFile($"Steam/userdata/{id1}/730/local/cfg/cs2_video.txt", "video");
        _dir.WriteFile($"Steam/userdata/{id1}/config/localconfig.vdf", """
            "UserLocalConfigStore" { "Software" { "Valve" { "Steam" { "apps" { "730" { "LaunchOptions" "-novid +exec novatune" } } } } } }
            """);
        Directory.CreateDirectory(_dir.Combine("Steam", "userdata", id2));
        return steam;
    }

    [Fact]
    public void FindsLibraries_SkipsMissingOnes()
    {
        var scanner = new SteamScanner(CreateSteam());
        var libs = scanner.GetLibraries();

        Assert.Equal(2, libs.Count);
        Assert.DoesNotContain(libs, l => l.EndsWith("Missing", StringComparison.Ordinal));
    }

    [Fact]
    public void ReadsAppsFromManifests()
    {
        var apps = new SteamScanner(CreateSteam()).GetApps();

        Assert.Equal(["Counter-Strike 2", "Dota 2"], apps.Select(a => a.Name).ToArray());
        var cs2 = apps.Single(a => a.AppId == 730);
        Assert.True(cs2.IsFullyInstalled);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), cs2.LastUpdated);
        Assert.EndsWith(Path.Combine("common", "Counter-Strike Global Offensive"), cs2.InstallDir);
    }

    [Fact]
    public void OldLibraryFormat_IsSupported()
    {
        var steam = _dir.Combine("Steam");
        var lib = _dir.Combine("Old");
        Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
        Directory.CreateDirectory(Path.Combine(lib, "steamapps"));
        _dir.WriteFile("Steam/steamapps/libraryfolders.vdf", $$"""
            "LibraryFolders" { "TimeNextStatsReport" "123" "ContentStatsID" "-1" "1" "{{Esc(lib)}}" }
            """);

        Assert.Equal(2, new SteamScanner(steam).GetLibraries().Count);
    }

    [Fact]
    public void BrokenManifest_IsSkippedWithWarning()
    {
        var steam = CreateSteam();
        _dir.WriteFile("Steam/steamapps/appmanifest_999.acf", "\"AppState\" { \"appid\" ");
        var scanner = new SteamScanner(steam);

        var apps = scanner.GetApps();

        Assert.Equal(2, apps.Count);
        Assert.NotEmpty(scanner.Warnings);
    }

    [Fact]
    public void ManifestWithPathTraversal_IsRejected()
    {
        var steam = CreateSteam();
        _dir.WriteFile("Steam/steamapps/appmanifest_1.acf", "\"AppState\" { \"appid\" \"1\" \"name\" \"x\" \"installdir\" \"..\\\\..\\\\Windows\" }");

        Assert.DoesNotContain(new SteamScanner(steam).GetApps(), a => a.AppId == 1);
    }

    [Fact]
    public void Cs2_IsLocated_WithUserConfigsAndLaunchOptions()
    {
        var cs2 = Cs2Locator.Locate(CreateSteam(), null);

        Assert.NotNull(cs2);
        Assert.NotNull(cs2.ExePath);
        Assert.False(cs2.FoundManually);
        var user = Assert.Single(cs2.Users);
        Assert.Equal("first", user.User.AccountName);
        Assert.Equal("-novid +exec novatune", user.LaunchOptions);
        Assert.Single(user.Files);
        Assert.Equal(2, cs2.ConfigFilesForBackup().Count);
    }

    [Fact]
    public void Cs2_NotInstalled_ReturnsNull()
    {
        Assert.Null(Cs2Locator.Locate(CreateSteam(withCs2InSecondLibrary: false), null));
    }

    [Fact]
    public void NoSteam_ReturnsNullWithoutThrowing()
    {
        Assert.Null(Cs2Locator.Locate(_dir.Combine("NoSteamHere"), null));
        Assert.Null(Cs2Locator.Locate(null, null));
        Assert.Empty(new SteamScanner(_dir.Combine("NoSteamHere")).GetApps());
    }

    [Fact]
    public void ManualPath_AcceptsNestedFolder_AndRejectsWrongFolder()
    {
        CreateSteam();
        var nested = _dir.Combine("SteamLibrary", "steamapps", "common", "Counter-Strike Global Offensive", "game", "bin");

        var found = Cs2Locator.Locate(null, nested);
        Assert.NotNull(found);
        Assert.True(found.FoundManually);
        Assert.EndsWith("Counter-Strike Global Offensive", found.GameDir);

        Assert.Null(Cs2Locator.Locate(null, _dir.Path));
    }
}

public sealed class Cs2AdvisorTests
{
    private static HardwareSummary Hw(ulong? vram, bool integrated = false, bool laptop = false, int threads = 12) => new()
    {
        CpuThreads = threads,
        RamTotalBytes = 16UL * 1024 * 1024 * 1024,
        Gpus = [new GpuSummary("NVIDIA GeForce RTX 3060", vram, integrated, null, null)],
        IsLaptop = laptop,
        Monitors = [new MonitorSummary("M", 1920, 1080, 144, true)],
    };

    [Theory]
    [InlineData(2UL, false, GpuTier.Low)]
    [InlineData(6UL, false, GpuTier.Mid)]
    [InlineData(12UL, false, GpuTier.High)]
    [InlineData(12UL, true, GpuTier.Low)]
    public void ClassifiesGpuByVram(ulong gb, bool integrated, GpuTier expected)
    {
        Assert.Equal(expected, Cs2Advisor.ClassifyGpu(new GpuSummary("x", gb * 1024 * 1024 * 1024, integrated, null, null)));
    }

    [Fact]
    public void UnknownVram_IsUnknownTier()
    {
        Assert.Equal(GpuTier.Unknown, Cs2Advisor.ClassifyGpu(new GpuSummary("x", null, false, null, null)));
    }

    [Fact]
    public void LaptopProfile_CapsFpsAtRefreshRate()
    {
        var hw = Hw(6UL * 1024 * 1024 * 1024, laptop: true);
        Assert.Equal(Cs2Profile.Laptop, Cs2Advisor.SuggestProfile(hw));
        Assert.Equal(144, Cs2Advisor.SuggestedFpsMax(Cs2Profile.Laptop, hw));
        Assert.Contains("fps_max \"144\"", Cs2Advisor.BuildConfig(Cs2Profile.Laptop, 144, DateTimeOffset.Now));
    }

    [Fact]
    public void Readiness_FlagsTooFewThreads()
    {
        var checks = Cs2Advisor.Readiness(new Cs2ReadinessInput(Hw(8UL << 30, threads: 2), null, true, false, true, 5, 40));
        Assert.Contains(checks, c => c.Level == Analysis.CriterionLevel.Bad);
    }

    [Fact]
    public void CompetitiveProfile_IncludesReflexOnlyForNvidia()
    {
        Assert.Contains(Cs2Advisor.Recommend(Cs2Profile.Competitive, Hw(8UL << 30)), r => r.Setting.Contains("Reflex"));
        var amd = Hw(8UL << 30) with { Gpus = [new GpuSummary("AMD Radeon RX 6600", 8UL << 30, false, null, null)] };
        Assert.DoesNotContain(Cs2Advisor.Recommend(Cs2Profile.Competitive, amd), r => r.Setting.Contains("Reflex"));
    }
}

public sealed class MonitorRefreshTests
{
    [Theory]
    [InlineData(60, 144, true)]
    [InlineData(59, 60, false)]
    [InlineData(144, 144, false)]
    public void BelowMaxRefresh_IgnoresOneHzRounding(int current, int max, bool expected)
    {
        Assert.Equal(expected, new NovaTune.Core.Hardware.MonitorSummary("M", 1920, 1080, current, true, max).BelowMaxRefresh);
    }

    [Fact]
    public void Readiness_FlagsMonitorBelowMax()
    {
        var hw = new NovaTune.Core.Hardware.HardwareSummary { Monitors = [new("M", 1920, 1080, 60, true, 165)] };
        var checks = Cs2Advisor.Readiness(new Cs2ReadinessInput(hw, null, null, null, null, null, null));
        Assert.Contains(checks, c => c.Level == NovaTune.Core.Analysis.CriterionLevel.Bad && c.Detail.Contains("165"));
    }
}
