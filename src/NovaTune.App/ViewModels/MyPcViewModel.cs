using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovaTune.App.Services;
using NovaTune.Core.Hardware;
using NovaTune.Core.Localization;

namespace NovaTune.App.ViewModels;

/// <summary>Подробные характеристики ПК по разделам. Все значения — из WMI, реестра и Win32 API.</summary>
public sealed partial class MyPcViewModel : PageViewModel
{
    private HardwareReport? _report;

    public override string Title => Loc.T("Мой ПК", "My PC");

    public override string Subtitle => Loc.T("Характеристики из WMI, реестра и Windows API", "Specifications from WMI, registry and Windows API");

    public ObservableCollection<InfoSection> Sections { get; } = [];

    [ObservableProperty] private string _collectedAt = "";

    protected override Task OnNavigatedToAsync(bool firstTime) => firstTime ? LoadAsync(false) : Task.CompletedTask;

    [RelayCommand]
    private Task Refresh() => LoadAsync(true);

    private async Task LoadAsync(bool refresh)
    {
        IsLoading = true;
        ErrorText = null;
        try
        {
            _report = await S.Hardware.GetAsync(refresh, PageCts.Token);
            Sections.Clear();
            foreach (var s in _report.Sections)
            {
                Sections.Add(s);
            }

            CollectedAt = Loc.T("Собрано: ", "Collected: ") + _report.CollectedAt.LocalDateTime.ToString("g");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void Copy()
    {
        if (_report is null)
        {
            return;
        }

        try
        {
            Clipboard.SetText(_report.ToPlainText());
            S.Toasts.Show(Loc.T("Сведения скопированы в буфер обмена", "Details copied to clipboard"), DialogKind.Success);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            // Буфер обмена может быть занят другой программой.
            S.Log.Warn("Буфер обмена занят", ex);
            S.Toasts.Show(Loc.T("Буфер обмена занят другой программой — попробуйте ещё раз", "Clipboard is busy — try again"), DialogKind.Warning);
        }
    }
}
