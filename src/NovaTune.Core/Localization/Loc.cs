using System.ComponentModel;

namespace NovaTune.Core.Localization;

public enum AppLanguage
{
    Ru,
    En,
}

/// <summary>
/// Текущий язык интерфейса. Тексты хранятся парами «рус/англ» прямо в месте использования,
/// а не в таблицах ключей, — так перевод не теряется и его легко проверить при чтении кода.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    private AppLanguage _language = AppLanguage.Ru;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Вызывается после смены языка (после PropertyChanged).</summary>
    public event EventHandler? LanguageChanged;

    public AppLanguage Language
    {
        get => _language;
        set
        {
            if (_language == value)
            {
                return;
            }

            _language = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public static bool IsEnglish => Instance.Language == AppLanguage.En;

    /// <summary>Возвращает текст на текущем языке.</summary>
    public static string T(string ru, string en) => IsEnglish ? en : ru;
}

/// <summary>
/// Текст на двух языках. Значение вычисляется в момент показа, поэтому описания модулей
/// сразу переключаются вместе с языком.
/// </summary>
public sealed record LocText(string Ru, string En)
{
    public static LocText Same(string text) => new(text, text);

    public string Value => Loc.T(Ru, En);

    public override string ToString() => Value;

    public static implicit operator string(LocText text) => text.Value;
}
