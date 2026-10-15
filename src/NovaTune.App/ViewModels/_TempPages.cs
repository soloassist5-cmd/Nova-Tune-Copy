namespace NovaTune.App.ViewModels;
// ВРЕМЕННО: заглушки для промежуточной сборки, удаляются по мере реализации страниц.
public sealed class MyPcViewModel() : PageViewModel { public override string Title => "MyPc"; public override string Subtitle => ""; }
public sealed class MonitoringViewModel() : PageViewModel { public override string Title => "Monitoring"; public override string Subtitle => ""; }
public sealed class OptimizeViewModel(MainViewModel main) : PageViewModel { public override string Title => "Optimize"; public override string Subtitle => ""; }
public sealed class Cs2ViewModel(MainViewModel main) : PageViewModel { public override string Title => "Cs2"; public override string Subtitle => ""; }
public sealed class GameCenterViewModel() : PageViewModel { public override string Title => "GameCenter"; public override string Subtitle => ""; }
public sealed class StartupViewModel() : PageViewModel { public override string Title => "Startup"; public override string Subtitle => ""; }
public sealed class CleanupViewModel() : PageViewModel { public override string Title => "Cleanup"; public override string Subtitle => ""; }
public sealed class BackupViewModel() : PageViewModel { public override string Title => "Backup"; public override string Subtitle => ""; }
public sealed class ReportsViewModel() : PageViewModel { public override string Title => "Reports"; public override string Subtitle => ""; }
public sealed class SettingsViewModel(MainViewModel main) : PageViewModel { public override string Title => "Settings"; public override string Subtitle => ""; }
