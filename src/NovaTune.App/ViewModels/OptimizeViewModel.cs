using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovaTune.App.Services;
using NovaTune.Core.Analysis;
using NovaTune.Core.Elevation;
using NovaTune.Core.Hardware;
using NovaTune.Core.Localization;
using NovaTune.Core.Optimization;
using NovaTune.Platform;

namespace NovaTune.App.ViewModels;

/// <summary>Карточка модуля оптимизации: состояние из системы, варианты, предпросмотр и применение через бэкап.</summary>
public sealed partial class ModuleCard : ObservableObject
{
    private readonly OptimizeViewModel _owner;

    public ModuleCard(IOptimizationModule module, OptimizeViewModel owner)
    {
        Module = module;
        _owner = owner;
    }

    public IOptimizationModule Module { get; }

    public string Title => Module.Title;

    public string Description => Module.Description;

    public string Benefit => Module.Benefit;

    public string Risks => Module.Risks;

    public string Category => Module.Category switch
    {
        OptimizationCategory.Gaming => Loc.T("Игры", "Gaming"),
        OptimizationCategory.Power => Loc.T("Питание", "Power"),
        _ => Loc.T("Графика", "Graphics"),
    };

    public bool RequiresAdmin => Module.RequiresAdmin;

    public bool RequiresRestart => Module.RequiresRestart;

    public bool InSafe => Module.IncludedInSafeOptimize;

    [ObservableProperty] private string _state = "";
    [ObservableProperty] private string? _note;
    [ObservableProperty] private bool _isRecommended;
    [ObservableProperty] private bool _isAvailable = true;
    [ObservableProperty] private string? _unavailableReason;
    [ObservableProperty] private ModuleOption? _selectedOption;
    [ObservableProperty] private string? _optionWarning;

    public ObservableCollection<ModuleOption> Options { get; } = [];

    partial void OnSelectedOptionChanged(ModuleOption? value) => OptionWarning = value?.Warning;

    public void Refresh(SystemContext context)
    {
        var availability = Module.CheckAvailability(context);
        IsAvailable = availability.IsAvailable;
        UnavailableReason = availability.Reason;
        var state = Module.ReadState(AppServices.Current.Store, context);
        State = state.CurrentDisplay;
        Note = state.Note;
        IsRecommended = state.IsRecommendedState;
        Options.Clear();
        foreach (var o in Module.GetOptions(AppServices.Current.Store, context))
        {
            Options.Add(o);
        }

        SelectedOption = Options.FirstOrDefault(o => o.IsRecommended) ?? Options.FirstOrDefault();
    }

    [RelayCommand]
    private Task Apply() => _owner.ApplyModuleAsync(this);
}

public sealed partial class VolumeRow(VolumeSummary volume, OptimizeViewModel owner) : ObservableObject
{
    public VolumeSummary Volume { get; } = volume;

    public string Title => $"{Volume.Letter}:  {Volume.Label}".Trim();

    public string Detail => Loc.T($"занято {Volume.UsedPercent:0}% · свободно {Units.Bytes(Volume.FreeBytes)} из {Units.Bytes(Volume.TotalBytes)} · {Volume.FileSystem}", $"{Volume.UsedPercent:0}% used · {Units.Bytes(Volume.FreeBytes)} free of {Units.Bytes(Volume.TotalBytes)} · {Volume.FileSystem}");

    public double UsedPercent => Volume.UsedPercent;

    public string Type => Volume.IsSsd switch { true => "SSD", false => "HDD", _ => Loc.T("тип не определён", "type unknown") };

    public bool IsSsd => Volume.IsSsd == true;

    public bool IsHdd => Volume.IsSsd == false;

    public bool IsUnknown => Volume.IsSsd is null;

    [RelayCommand]
    private Task Retrim() => owner.OptimizeVolumeAsync(this, ElevatedOperation.VolumeReTrim);

    [RelayCommand]
    private Task Defrag() => owner.OptimizeVolumeAsync(this, ElevatedOperation.VolumeOptimizeHdd);

    [RelayCommand]
    private static void OpenOptimizeDrives() => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("dfrgui.exe") { UseShellExecute = true });
}

public sealed record Recommendation(string Title, string Detail, CriterionLevel Level);

public sealed record ProcessRow(string Name, string Cpu, string Memory, bool IsSystem);

public sealed partial class OptimizeViewModel(MainViewModel main) : PageViewModel
{
    public override string Title => Loc.T("Оптимизация", "Optimize");

    public override string Subtitle => Loc.T("Только безопасные, понятные и обратимые изменения — с предпросмотром и бэкапом", "Only safe, clear and reversible changes — with preview and backup");

    [ObservableProperty] private string _tab = "modules";

    public ObservableCollection<ModuleCard> Modules { get; } = [];

    public ObservableCollection<Recommendation> Recommendations { get; } = [];

    public ObservableCollection<VolumeRow> Volumes { get; } = [];

    public ObservableCollection<InfoSectionRow> Diagnostics { get; } = [];

    public ObservableCollection<ProcessRow> Processes { get; } = [];

    public ObservableCollection<string> NetworkResults { get; } = [];

    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string? _scanSummary;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _pingTargets = "";
    [ObservableProperty] private bool _isPinging;

    private SystemContext? _context;

    protected override async Task OnNavigatedToAsync(bool firstTime)
    {
        if (firstTime)
        {
            PingTargets = string.Join(", ", S.Settings.Current.PingTargets);
            foreach (var m in S.Modules)
            {
                Modules.Add(new ModuleCard(m, this));
            }
        }

        IsLoading = true;
        try
        {
            _context = await S.GetContextAsync(ct: PageCts.Token);
            foreach (var card in Modules)
            {
                card.Refresh(_context);
            }

            var hw = await S.Hardware.GetAsync(false, PageCts.Token);
            Volumes.Clear();
            foreach (var v in hw.Summary.Volumes)
            {
                Volumes.Add(new VolumeRow(v, this));
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnTabChanged(string value)
    {
        var tab = value;
        if (tab == "diagnostics" && Diagnostics.Count == 0)
        {
            _ = LoadDiagnosticsAsync();
        }
        else if (tab == "processes" && Processes.Count == 0)
        {
            _ = LoadProcessesAsync();
        }
    }

    public async Task ApplyModuleAsync(ModuleCard card)
    {
        if (_context is null || card.SelectedOption is not { } option)
        {
            return;
        }

        await RunSafeAsync(async () =>
        {
            var changes = card.Module.Plan(S.Store, _context, option.Id);
            var warning = card.Module.RequiresRestart ? Loc.T("Изменение вступит в силу после перезагрузки.", "The change takes effect after a restart.") : null;
            if (option.Warning is not null)
            {
                warning = (warning is null ? "" : warning + " ") + option.Warning;
            }

            await ChangeRunner.ApplyAsync(card.Title + ": " + option.Title, changes, warning);
            _context = await S.GetContextAsync(refresh: card.Module.Id == "hags");
            card.Refresh(_context);
        }, Loc.T("Не удалось применить", "Failed to apply"));
    }

    // ───────── Сканирование и Safe Optimize ─────────

    [RelayCommand]
    private Task Scan() => RunSafeAsync(async () =>
    {
        IsScanning = true;
        try
        {
            var result = await SystemScan.RunAsync(false, PageCts.Token);
            _context = result.Context;
            Recommendations.Clear();
            foreach (var c in result.Health.Criteria.Where(c => c.Recommendation is not null))
            {
                Recommendations.Add(new Recommendation(c.Title, c.Detail + " " + c.Recommendation, c.Level));
            }

            foreach (var card in Modules)
            {
                card.Refresh(_context);
                if (!card.IsRecommended && card.IsAvailable)
                {
                    Recommendations.Add(new Recommendation(card.Title, card.State + (card.Note is null ? "" : " " + card.Note), CriterionLevel.Warning));
                }
            }

            var safe = OptimizationCatalog.PlanSafeOptimize(S.Modules, S.Store, _context);
            ScanSummary = Loc.T(
                $"Оценка {result.Health.Score}/100. Рекомендаций: {Recommendations.Count}. Безопасных изменений для Safe Optimize: {safe.Sum(p => p.Changes.Count)}.",
                $"Score {result.Health.Score}/100. Recommendations: {Recommendations.Count}. Safe Optimize changes available: {safe.Sum(p => p.Changes.Count)}.");
        }
        finally
        {
            IsScanning = false;
        }
    }, Loc.T("Ошибка сканирования", "Scan error"));

    [RelayCommand]
    private Task SafeOptimize() => RunSafeAsync(async () =>
    {
        _context ??= await S.GetContextAsync();
        var plan = OptimizationCatalog.PlanSafeOptimize(S.Modules, S.Store, _context);
        await ChangeRunner.ApplyAsync("Safe Optimize", plan.SelectMany(p => p.Changes).ToList());
        foreach (var card in Modules)
        {
            card.Refresh(_context);
        }
    }, "Safe Optimize");

    [RelayCommand]
    private void OpenBackups() => main.Navigate("backup");

    // ───────── Накопители ─────────

    public Task OptimizeVolumeAsync(VolumeRow row, ElevatedOperation operation) => RunSafeAsync(async () =>
    {
        var isTrim = operation == ElevatedOperation.VolumeReTrim;
        var text = isTrim
            ? Loc.T($"Будет выполнен TRIM для {row.Volume.Letter}: штатной утилитой Windows (defrag {row.Volume.Letter}: /L). Дефрагментация SSD не выполняется. Нужны права администратора.", $"TRIM will run on {row.Volume.Letter}: using the built-in Windows tool (defrag {row.Volume.Letter}: /L). SSDs are never defragmented. Administrator rights required.")
            : Loc.T($"Будет выполнена штатная оптимизация HDD {row.Volume.Letter}: (defrag {row.Volume.Letter}: /O). Может занять долгое время. Нужны права администратора.", $"Built-in HDD optimization will run on {row.Volume.Letter}: (defrag {row.Volume.Letter}: /O). This may take a long time. Administrator rights required.");
        if (!await S.Dialogs.ConfirmAsync(isTrim ? "TRIM" : Loc.T("Оптимизация HDD", "HDD optimization"), text, Loc.T("Запустить", "Run"), kind: DialogKind.Info))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await S.Elevation.RunAsync(new ElevatedTaskRequest { Operation = operation, DriveLetter = row.Volume.Letter });
            S.Journal.Add("storage", (isTrim ? "TRIM " : "Optimize ") + row.Volume.Letter + ":", result.Success, result.Error);
            await S.Dialogs.AlertAsync(result.Success ? Loc.T("Готово", "Done") : Loc.T("Не выполнено", "Not completed"), result.Success ? Loc.T("Операция завершена.", "Operation finished.") : result.Error ?? "", result.Success ? DialogKind.Success : DialogKind.Error);
        }
        catch (Core.Common.ElevationCancelledException)
        {
            S.Toasts.Show(Loc.T("Запрос прав администратора отклонён", "Administrator request declined"));
        }
        finally
        {
            IsBusy = false;
        }
    }, Loc.T("Ошибка оптимизации диска", "Drive optimization error"));

    // ───────── Сеть ─────────

    [RelayCommand]
    private Task RunNetwork() => RunSafeAsync(async () =>
    {
        IsPinging = true;
        NetworkResults.Clear();
        try
        {
            var targets = PingTargets.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries).Take(5).ToList();
            if (targets.Count == 0 || targets.Any(t => t.Length > 253 || t.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or ':'))))
            {
                NetworkResults.Add(Loc.T("Укажите адреса или имена серверов через запятую.", "Enter addresses or host names separated by commas."));
                return;
            }

            S.Settings.Current.PingTargets = targets;
            S.Settings.Save();

            var conn = await S.Network.CheckConnectivityAsync(S.Http, PageCts.Token);
            NetworkResults.Add(Loc.T("Подключение: ", "Connectivity: ") + conn.Detail);
            var dns = await S.Network.ResolveAsync("www.microsoft.com", PageCts.Token);
            NetworkResults.Add(dns.Error is null
                ? Loc.T($"DNS: www.microsoft.com разрешён за {dns.ElapsedMs:0} мс (с учётом кэша)", $"DNS: www.microsoft.com resolved in {dns.ElapsedMs:0} ms (cache included)")
                : "DNS: " + dns.Error);

            foreach (var target in targets)
            {
                var p = await S.Network.PingAsync(target, 20, 1000, 200, null, PageCts.Token);
                NetworkResults.Add(p.Error is not null && p.Received == 0
                    ? $"{target}: {p.Error}"
                    : string.Create(CultureInfo.CurrentCulture, $"{target} ({p.ResolvedAddress}): {Loc.T("мин/сред/макс", "min/avg/max")} {p.MinMs:0}/{p.AvgMs:0}/{p.MaxMs:0} ms, {Loc.T("джиттер", "jitter")} {p.JitterMs:0.0} ms, {Loc.T("потери", "loss")} {p.LossPercent:0}%"));
            }

            S.Journal.Add("network", Loc.T("Диагностика сети", "Network diagnostics"), true, string.Join("; ", NetworkResults));
        }
        finally
        {
            IsPinging = false;
        }
    }, Loc.T("Ошибка диагностики сети", "Network diagnostics error"));

    // ───────── Диагностика Windows и процессы ─────────

    private async Task LoadDiagnosticsAsync()
    {
        IsBusy = true;
        try
        {
            var sections = await S.Diagnostics.CollectAsync(PageCts.Token);
            Diagnostics.Clear();
            foreach (var s in sections)
            {
                Diagnostics.Add(new InfoSectionRow(s.Title, s.Items.Select(i => (i.Label, i.DisplayValue)).ToList()));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            S.Log.Error("Диагностика Windows", ex);
            Diagnostics.Add(new InfoSectionRow(Loc.T("Ошибка", "Error"), [(ex.Message, "")]));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private Task RefreshDiagnostics() => LoadDiagnosticsAsync();

    [RelayCommand]
    private async Task LoadProcessesAsync()
    {
        IsBusy = true;
        try
        {
            var list = await S.Processes.SampleAsync(TimeSpan.FromSeconds(1.5), 15, PageCts.Token);
            Processes.Clear();
            foreach (var p in list)
            {
                Processes.Add(new ProcessRow(p.Name, p.CpuPercent.ToString("0.0", CultureInfo.CurrentCulture) + "%", Units.Bytes(p.WorkingSetBytes), p.IsSystem));
            }
        }
        catch (OperationCanceledException)
        {
            // Ушли со страницы.
        }
        finally
        {
            IsBusy = false;
        }
    }
}

public sealed record InfoSectionRow(string Title, IReadOnlyList<(string Label, string Value)> Rows)
{
    public IReadOnlyList<KeyValuePair<string, string>> Pairs => Rows.Select(r => new KeyValuePair<string, string>(r.Label, r.Value)).ToList();
}
