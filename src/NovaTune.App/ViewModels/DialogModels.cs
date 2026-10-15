using NovaTune.Core.Backup;
using NovaTune.Core.Localization;

namespace NovaTune.App.ViewModels;

/// <summary>Строка предпросмотра: что меняется, где, было → станет.</summary>
public sealed record PreviewRow(string Title, string Target, string Current, string New, string? Note);

public sealed record PreviewList(IReadOnlyList<PreviewRow> Rows)
{
    public static PreviewList From(IEnumerable<ChangePreview> previews) =>
        new(previews.Select(p => new PreviewRow(
            p.Change.Title,
            p.Change.TargetDisplay,
            p.CurrentDisplay,
            p.NewDisplay,
            Note(p))).ToList());

    private static string? Note(ChangePreview p)
    {
        var notes = new List<string>();
        if (p.AlreadyInTargetState)
        {
            notes.Add(Loc.T("уже в нужном состоянии — будет пропущено", "already in the target state — will be skipped"));
        }

        if (p.Change.RequiresAdmin)
        {
            notes.Add(Loc.T("потребуются права администратора (запрос UAC)", "administrator rights required (UAC prompt)"));
        }

        if (p.Change.RequiresRestart)
        {
            notes.Add(Loc.T("вступит в силу после перезагрузки", "takes effect after restart"));
        }

        return notes.Count == 0 ? null : string.Join("; ", notes);
    }
}

/// <summary>Список строк для диалога (итоги операции).</summary>
public sealed record TextLines(IReadOnlyList<string> Lines);
