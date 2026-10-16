using System.Diagnostics;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using NovaTune.Core.Localization;
using NovaTune.Core.Tools;

namespace NovaTune.App.ViewModels;

/// <summary>Тест реакции: ждёте зелёный цвет и кликаете. Время — по Stopwatch от смены цвета до нажатия кнопки мыши.</summary>
public sealed partial class ToolsViewModel
{
    private readonly ReactionStats _reaction = new();
    private readonly Stopwatch _reactionClock = new();
    private DispatcherTimer? _reactionTimer;

    /// <summary>idle | wait | go | early | result</summary>
    [ObservableProperty] private string _reactionState = "idle";
    [ObservableProperty] private string _reactionText = Loc.T("Кликните, чтобы начать", "Click to start");
    [ObservableProperty] private string _reactionStats = "";

    /// <summary>Вызывается из представления по нажатию кнопки мыши (а не по отпусканию — так точнее).</summary>
    public void ReactionPress()
    {
        switch (ReactionState)
        {
            case "wait":
                _reactionTimer?.Stop();
                ReactionState = "early";
                ReactionText = Loc.T("Рано! Кликните, чтобы попробовать снова", "Too early! Click to try again");
                break;

            case "go":
                _reactionClock.Stop();
                var ms = _reactionClock.Elapsed.TotalMilliseconds;
                _reaction.Add(ms);
                ReactionState = "result";
                ReactionText = $"{ms:0} ms — " + Loc.T("кликните для следующей попытки", "click for the next try");
                ReactionStats = Loc.T(
                    $"Попыток: {_reaction.Results.Count} · лучшее: {_reaction.Best:0} мс · среднее за 5: {_reaction.AverageLast5:0} мс",
                    $"Attempts: {_reaction.Results.Count} · best: {_reaction.Best:0} ms · average of 5: {_reaction.AverageLast5:0} ms");
                break;

            default:
                StartReactionRound();
                break;
        }
    }

    private void StartReactionRound()
    {
        ReactionState = "wait";
        ReactionText = Loc.T("Ждите зелёный…", "Wait for green…");

        // Случайная задержка нужна, чтобы нельзя было «угадать» момент; на результат она не влияет.
        _reactionTimer?.Stop();
        _reactionTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(Random.Shared.Next(1500, 4000)) };
        _reactionTimer.Tick += (_, _) =>
        {
            _reactionTimer.Stop();
            ReactionState = "go";
            ReactionText = Loc.T("Жми!", "Click!");
            _reactionClock.Restart();
        };
        _reactionTimer.Start();
    }

    public void ResetReaction()
    {
        _reactionTimer?.Stop();
        _reaction.Clear();
        ReactionState = "idle";
        ReactionText = Loc.T("Кликните, чтобы начать", "Click to start");
        ReactionStats = "";
    }
}
