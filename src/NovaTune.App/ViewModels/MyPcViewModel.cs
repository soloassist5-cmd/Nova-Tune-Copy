using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovaTune.App.Services;
using NovaTune.Core.Hardware;
using NovaTune.Core.Localization;

namespace NovaTune.App.ViewModels;

/// <summary>Блок «Мой ПК», который можно свернуть: в свёрнутом виде виден заголовок и краткая сводка.</summary>
public sealed partial class SectionCard : ObservableObject
{
    private readonly Action<SectionCard> _changed;

    [ObservableProperty]
    private bool _isExpanded;

    public SectionCard(InfoSection section, bool expanded, Action<SectionCard> changed)
    {
        Section = section;
        _isExpanded = expanded;
        _changed = changed;
        var children = section.Children ?? [];
        Summary = children.Count > 0
            ? string.Join("  ·  ", children.Select(c => c.Title))
            : section.Items.Count switch
            {
                0 => "",
                1 => section.Items[0].DisplayValue,
                _ => section.Items[0].DisplayValue + Loc.T($"  ·  ещё {section.Items.Count - 1}", $"  ·  {section.Items.Count - 1} more"),
            };
    }

    public InfoSection Section { get; }

    public string Title => Section.Title;

    public IReadOnlyList<InfoItem> Items => Section.Items;

    public IReadOnlyList<InfoSection> Children => Section.Children ?? [];

    public string Summary { get; }

    partial void OnIsExpandedChanged(bool value) => _changed(this);

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;
}

/// <summary>Подробные характеристики ПК по разделам. Все значения — из WMI, реестра и Win32 API.</summary>
public sealed partial class MyPcViewModel : PageViewModel
{
    private HardwareReport? _report;

    public override string Title => Loc.T("Мой ПК", "My PC");

    public override string Subtitle => Loc.T("Характеристики из WMI, реестра и Windows API", "Specifications from WMI, registry and Windows API");

    /// <summary>Две колонки, каждая карточка своей высоты (раскладка «кирпичами» по объёму содержимого).</summary>
    public ObservableCollection<SectionCard> LeftSections { get; } = [];

    public ObservableCollection<SectionCard> RightSections { get; } = [];

    private IEnumerable<SectionCard> AllCards => LeftSections.Concat(RightSections);

    private bool _bulk;

    private HashSet<string> Collapsed => [.. S.Settings.Current.CollapsedSections ?? ["network"]];

    [RelayCommand]
    private void ExpandAll() => SetAll(true);

    [RelayCommand]
    private void CollapseAll() => SetAll(false);

    private void SetAll(bool expanded)
    {
        _bulk = true;
        foreach (var card in AllCards)
        {
            card.IsExpanded = expanded;
        }

        _bulk = false;
        SaveCollapsed();
    }

    private void OnCardChanged(SectionCard card)
    {
        if (!_bulk)
        {
            SaveCollapsed();
        }
    }

    /// <summary>Запоминаем, какие блоки свёрнуты, — при следующем открытии они останутся такими же.</summary>
    private void SaveCollapsed()
    {
        S.Settings.Current.CollapsedSections = AllCards.Where(c => !c.IsExpanded).Select(c => c.Section.Id).ToList();
        S.Settings.Save();
    }

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
            LeftSections.Clear();
            RightSections.Clear();
            int left = 0, right = 0;
            var collapsed = Collapsed;
            foreach (var section in _report.Sections)
            {
                var card = new SectionCard(section, !collapsed.Contains(section.Id), OnCardChanged);
                // Примерная высота: строки раздела + строки подразделов + заголовки.
                var weight = !card.IsExpanded ? 2 : 2 + section.Items.Count + (section.Children ?? []).Sum(c => 2 + c.Items.Count);
                if (left <= right)
                {
                    LeftSections.Add(card);
                    left += weight;
                }
                else
                {
                    RightSections.Add(card);
                    right += weight;
                }
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
