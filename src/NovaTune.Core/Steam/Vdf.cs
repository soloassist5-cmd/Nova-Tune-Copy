using System.Text;

namespace NovaTune.Core.Steam;

/// <summary>Узел формата Valve KeyValues (VDF/ACF): либо строковое значение, либо набор дочерних узлов.</summary>
public sealed class VdfNode
{
    private readonly List<VdfNode> _children = [];

    public VdfNode(string key, string? value = null)
    {
        Key = key;
        Value = value;
    }

    public string Key { get; }

    /// <summary>Строковое значение; null у узлов-секций.</summary>
    public string? Value { get; }

    public bool IsSection => Value is null;

    public IReadOnlyList<VdfNode> Children => _children;

    internal void Add(VdfNode child) => _children.Add(child);

    /// <summary>Первый дочерний узел с ключом (без учёта регистра — Steam сам непоследователен в регистре).</summary>
    public VdfNode? this[string key] => _children.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));

    public string? GetString(string key) => this[key]?.Value;

    /// <summary>Значение по пути ключей, например ("Software", "Valve", "Steam", "apps", "730", "LaunchOptions").</summary>
    public VdfNode? Path(params string[] keys)
    {
        var node = this;
        foreach (var key in keys)
        {
            node = node?[key];
            if (node is null)
            {
                return null;
            }
        }

        return node;
    }
}

public sealed class VdfParseException(string message, int line) : FormatException($"{message} (строка {line})")
{
    public int Line { get; } = line;
}

/// <summary>
/// Парсер текстового KeyValues. Поддерживает: строки в кавычках и без, escape-последовательности (\" \\ \n \t),
/// комментарии //, условия вида [$WIN32] (пропускаются), директивы #include/#base (пропускаются).
/// Возвращает корневой узел без ключа, содержащий секции верхнего уровня.
/// </summary>
public static class VdfParser
{
    public static VdfNode Parse(string text)
    {
        var reader = new Tokenizer(text);
        var root = new VdfNode("");
        ParseChildren(reader, root, isRoot: true);
        return root;
    }

    public static VdfNode ParseFile(string path) => Parse(File.ReadAllText(path, Encoding.UTF8));

    private static void ParseChildren(Tokenizer reader, VdfNode parent, bool isRoot)
    {
        while (true)
        {
            var token = reader.Next();
            if (token.Kind == TokenKind.End)
            {
                if (!isRoot)
                {
                    throw new VdfParseException("Неожиданный конец файла: не закрыта секция «" + parent.Key + "»", reader.Line);
                }

                return;
            }

            if (token.Kind == TokenKind.Close)
            {
                if (isRoot)
                {
                    throw new VdfParseException("Лишняя закрывающая скобка", reader.Line);
                }

                return;
            }

            if (token.Kind == TokenKind.Open)
            {
                throw new VdfParseException("Открывающая скобка без ключа", reader.Line);
            }

            if (token.Kind == TokenKind.Directive)
            {
                // #include/#base: строку с именем файла пропускаем — внешние файлы не подгружаем.
                reader.Next();
                continue;
            }

            var key = token.Text;
            var next = reader.Next();
            if (next.Kind == TokenKind.Condition)
            {
                next = reader.Next();
            }

            switch (next.Kind)
            {
                case TokenKind.Open:
                    {
                        var section = new VdfNode(key);
                        ParseChildren(reader, section, isRoot: false);
                        SkipCondition(reader);
                        parent.Add(section);
                        break;
                    }

                case TokenKind.String:
                    parent.Add(new VdfNode(key, next.Text));
                    SkipCondition(reader);
                    break;
                default:
                    throw new VdfParseException("После ключа «" + key + "» ожидалось значение или «{»", reader.Line);
            }
        }
    }

    private static void SkipCondition(Tokenizer reader)
    {
        if (reader.PeekKind() == TokenKind.Condition)
        {
            reader.Next();
        }
    }

    private enum TokenKind
    {
        String,
        Open,
        Close,
        Condition,
        Directive,
        End,
    }

    private readonly record struct Token(TokenKind Kind, string Text);

    private sealed class Tokenizer(string text)
    {
        private int _pos;
        private Token? _peeked;

        public int Line { get; private set; } = 1;

        public TokenKind PeekKind()
        {
            _peeked ??= Read();
            return _peeked.Value.Kind;
        }

        public Token Next()
        {
            if (_peeked is { } p)
            {
                _peeked = null;
                return p;
            }

            return Read();
        }

        private Token Read()
        {
            SkipWhitespaceAndComments();
            if (_pos >= text.Length)
            {
                return new Token(TokenKind.End, "");
            }

            var c = text[_pos];
            switch (c)
            {
                case '{':
                    _pos++;
                    return new Token(TokenKind.Open, "{");
                case '}':
                    _pos++;
                    return new Token(TokenKind.Close, "}");
                case '[':
                    {
                        var end = text.IndexOf(']', _pos);
                        if (end < 0)
                        {
                            throw new VdfParseException("Не закрыто условие [..]", Line);
                        }

                        var cond = text[(_pos + 1)..end];
                        _pos = end + 1;
                        return new Token(TokenKind.Condition, cond);
                    }

                case '"':
                    return new Token(TokenKind.String, ReadQuoted());
                case '#':
                    {
                        var word = ReadUnquoted();
                        return word is "#include" or "#base"
                            ? new Token(TokenKind.Directive, word)
                            : new Token(TokenKind.String, word);
                    }

                default:
                    return new Token(TokenKind.String, ReadUnquoted());
            }
        }

        private string ReadQuoted()
        {
            _pos++; // открывающая кавычка
            var sb = new StringBuilder();
            while (_pos < text.Length)
            {
                var c = text[_pos++];
                if (c == '"')
                {
                    return sb.ToString();
                }

                if (c == '\n')
                {
                    Line++;
                }

                if (c == '\\' && _pos < text.Length)
                {
                    var e = text[_pos++];
                    switch (e)
                    {
                        case 'n':
                            sb.Append('\n');
                            break;
                        case 't':
                            sb.Append('\t');
                            break;
                        case '"':
                        case '\\':
                            sb.Append(e);
                            break;
                        default:
                            // Неизвестная последовательность (например, неэкранированный путь «C:\Games») — сохраняем как есть.
                            sb.Append('\\').Append(e);
                            if (e == '\n')
                            {
                                Line++;
                            }

                            break;
                    }

                    continue;
                }

                sb.Append(c);
            }

            throw new VdfParseException("Не закрыта строка в кавычках", Line);
        }

        private string ReadUnquoted()
        {
            var start = _pos;
            while (_pos < text.Length && !char.IsWhiteSpace(text[_pos]) && text[_pos] is not ('{' or '}' or '"' or '['))
            {
                _pos++;
            }

            return text[start.._pos];
        }

        private void SkipWhitespaceAndComments()
        {
            while (_pos < text.Length)
            {
                var c = text[_pos];
                if (c == '\n')
                {
                    Line++;
                    _pos++;
                }
                else if (char.IsWhiteSpace(c) || c == '\uFEFF')
                {
                    _pos++;
                }
                else if (c == '/' && _pos + 1 < text.Length && text[_pos + 1] == '/')
                {
                    while (_pos < text.Length && text[_pos] != '\n')
                    {
                        _pos++;
                    }
                }
                else
                {
                    break;
                }
            }
        }
    }
}
