using System.Globalization;
using System.Text;

namespace GG2.Maps;

/// <summary>
/// A decoded GGON value. GGON has exactly two shapes: a string, and a map from
/// string keys to values.
/// </summary>
/// <remarks>
/// Lists are not a third shape — <c>Scripts/ggon/ggon_parse_tokens.gml</c>
/// decodes <c>[a, b]</c> into the map <c>{0: a, 1: b, length: 2}</c>, which is
/// why maps written by the builder and lists written by the compiler both come
/// back the same way. See <see cref="GgonMap.ToList"/>.
/// </remarks>
public abstract class GgonValue
{
    /// <summary>The string this value is, or null if it is a map.</summary>
    public string? AsString => (this as GgonString)?.Value;

    /// <summary>The map this value is, or null if it is a string.</summary>
    public GgonMap? AsMap => this as GgonMap;
}

public sealed class GgonString(string value) : GgonValue
{
    public string Value { get; } = value;

    public override string ToString() => Value;
}

public sealed class GgonMap : GgonValue
{
    private readonly Dictionary<string, GgonValue> _entries = [];

    public IReadOnlyDictionary<string, GgonValue> Entries => _entries;

    public int Count => _entries.Count;

    internal void Add(string key, GgonValue value) => _entries[key] = value;

    public bool TryGet(string key, out GgonValue value) => _entries.TryGetValue(key, out value!);

    public GgonValue? Get(string key) => _entries.GetValueOrDefault(key);

    /// <summary>The value at <paramref name="key"/> as a string, or null.</summary>
    public string? GetString(string key) => Get(key)?.AsString;

    /// <summary>
    /// The list form of a map carrying a <c>length</c> key and string indexes
    /// <c>"0"</c>..<c>"length-1"</c> — <c>ggon_map_to_list.gml</c>.
    /// </summary>
    /// <remarks>
    /// A missing index yields a null element rather than throwing, matching
    /// <c>ds_map_find_value</c>'s behaviour on an absent key. In practice the
    /// shipped maps are dense.
    /// </remarks>
    public IReadOnlyList<GgonValue?> ToList()
    {
        var lengthText = GetString("length")
            ?? throw new GgonException("map has no 'length' key and so is not a list");

        if (!int.TryParse(lengthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var length) || length < 0)
            throw new GgonException($"list length '{lengthText}' is not a non-negative integer");

        var list = new GgonValue?[length];
        for (var i = 0; i < length; i++)
            list[i] = Get(i.ToString(CultureInfo.InvariantCulture));

        return list;
    }
}

public sealed class GgonException(string message) : Exception(message);

/// <summary>
/// Gang Garrison Object Notation, transcribed from <c>Scripts/ggon/</c>.
/// </summary>
/// <remarks>
/// This is the format a map's <c>{ENTITIES}</c> block is written in. It is
/// JSON-shaped but not JSON: bare words are strings, quoting is single, there
/// are no numbers, and a trailing comma is not permitted.
/// </remarks>
public static class Ggon
{
    /// <summary>Decodes a complete GGON text.</summary>
    public static GgonValue Decode(string text) => new Parser(Tokenise(text)).ParseValue();

    // --- tokeniser (ggon_tokenise.gml) ---------------------------------------

    private enum TokenKind { Punctuation, String }

    private readonly record struct Token(TokenKind Kind, char Punctuation, string Text);

    private static bool IsIdentifierChar(char c) =>
        c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '.' or '+' or '-';

    private static List<Token> Tokenise(string text)
    {
        var tokens = new List<Token>();
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];

            switch (c)
            {
                case '{':
                case '}':
                case ':':
                case '[':
                case ']':
                case ',':
                    tokens.Add(new Token(TokenKind.Punctuation, c, string.Empty));
                    i++;
                    continue;
                case ' ':
                case '\t':
                case '\n':
                case '\r':
                    i++;
                    continue;
            }

            // Bare words. The reference calls these "identifiers" but they are
            // strings: {x: 2331} yields the string "2331", never a number.
            if (IsIdentifierChar(c))
            {
                var start = i;
                while (i < text.Length && IsIdentifierChar(text[i]))
                    i++;
                tokens.Add(new Token(TokenKind.String, '\0', text[start..i]));
                continue;
            }

            if (c == '\'')
            {
                i++;
                var sb = new StringBuilder();
                while (true)
                {
                    if (i >= text.Length)
                        throw new GgonException("unexpected end of text while parsing string");

                    var ch = text[i];
                    if (ch == '\'')
                        break;

                    if (ch == '\\')
                    {
                        i++;
                        if (i >= text.Length)
                            throw new GgonException("unexpected end of text while parsing string");

                        var esc = text[i];
                        sb.Append(esc switch
                        {
                            '\'' => '\'',
                            '\\' => '\\',
                            'n' => '\n',
                            'r' => '\r',
                            't' => '\t',
                            '0' => '\0',
                            _ => throw new GgonException($"unknown escape sequence \"backslash-{esc}\""),
                        });
                    }
                    else
                    {
                        sb.Append(ch);
                    }

                    i++;
                }

                i++; // past the closing quote
                tokens.Add(new Token(TokenKind.String, '\0', sb.ToString()));
                continue;
            }

            throw new GgonException($"unexpected character \"{c}\"");
        }

        return tokens;
    }

    // --- parser (ggon_parse_tokens.gml) --------------------------------------

    private sealed class Parser(List<Token> tokens)
    {
        private int _at;

        private bool AtEnd => _at >= tokens.Count;

        private Token Dequeue()
        {
            if (AtEnd)
                throw new GgonException("unexpected end of tokens");
            return tokens[_at++];
        }

        private Token Peek()
        {
            if (AtEnd)
                throw new GgonException("unexpected end of tokens");
            return tokens[_at];
        }

        private static string Describe(Token t) =>
            t.Kind == TokenKind.Punctuation ? t.Punctuation.ToString() : t.Text;

        public GgonValue ParseValue()
        {
            var token = Dequeue();

            if (token.Kind == TokenKind.String)
                return new GgonString(token.Text);

            if (token.Punctuation == '{')
                return ParseMapBody();

            if (token.Punctuation == '[')
                return ParseListBody();

            throw new GgonException($"unexpected token \"{Describe(token)}\"");
        }

        private GgonMap ParseMapBody()
        {
            var map = new GgonMap();

            if (Peek() is { Kind: TokenKind.Punctuation, Punctuation: '}' })
            {
                Dequeue();
                return map;
            }

            while (true)
            {
                var keyToken = Dequeue();
                if (keyToken.Kind != TokenKind.String)
                    throw new GgonException($"unexpected token \"{Describe(keyToken)}\" where a key was expected");

                var colon = Dequeue();
                if (colon is not { Kind: TokenKind.Punctuation, Punctuation: ':' })
                    throw new GgonException($"unexpected token \"{Describe(colon)}\" after key");

                map.Add(keyToken.Text, ParseValue());

                var next = Dequeue();
                if (next is { Kind: TokenKind.Punctuation, Punctuation: ',' })
                    continue;
                if (next is { Kind: TokenKind.Punctuation, Punctuation: '}' })
                    return map;

                throw new GgonException($"unexpected token \"{Describe(next)}\" after value");
            }
        }

        // A list decodes to a map: {"0": v0, ..., "length": n}. That is the
        // reference's representation, not a convenience of this port, and
        // GgonMap.ToList is the only thing that should care.
        private GgonMap ParseListBody()
        {
            var map = new GgonMap();

            if (Peek() is { Kind: TokenKind.Punctuation, Punctuation: ']' })
            {
                Dequeue();
                map.Add("length", new GgonString("0"));
                return map;
            }

            var length = 0;
            while (true)
            {
                map.Add(length.ToString(CultureInfo.InvariantCulture), ParseValue());
                length++;

                var next = Dequeue();
                if (next is { Kind: TokenKind.Punctuation, Punctuation: ',' })
                    continue;
                if (next is { Kind: TokenKind.Punctuation, Punctuation: ']' })
                {
                    map.Add("length", new GgonString(length.ToString(CultureInfo.InvariantCulture)));
                    return map;
                }

                throw new GgonException($"unexpected token \"{Describe(next)}\" after value");
            }
        }
    }
}
