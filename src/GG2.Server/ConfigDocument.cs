using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GG2.Server;

/// <summary>
/// A configuration file's JSON: one object per section, one property per key.
/// </summary>
/// <remarks>
/// <para>
/// <c>{"Server": {"ServerName": "A Real Server"}, "Bots": {"Enabled": true}}</c>.
/// <see cref="ServerSettings"/> names every section and key; this class is only
/// the grammar around them. <c>docs/CONFIG.md</c> has the rules.
/// </para>
/// <para>
/// <b>Section and key names are case-insensitive; values are not.</b> An
/// operator who wrote <c>"server"</c> or <c>"playerlimit"</c> means the same
/// thing, and a password is data. ⛔ <b>A name given twice is refused</b>, case
/// folded: which of two <c>ServerName</c>s wins would otherwise be a rule nobody
/// reads.
/// </para>
/// <para>
/// <b>A value is typed as its setting is</b> — a boolean is <c>true</c>, a
/// number a number, a list an array — but <see cref="TryGet"/> hands every one
/// back as the setting's TEXT, because <see cref="ServerSetting.Set"/> takes
/// text. A string where a number belongs is read the same way and still
/// validated, so <c>"PlayerLimit": "16"</c> works and <c>"sixteen"</c> is refused.
/// <c>null</c> means absent.
/// </para>
/// <para>
/// <b>Unknown sections and keys are kept and otherwise ignored</b>, so
/// <see cref="ServerConfigFile.Write"/> preserves them. JSON has no comments; a
/// key nobody reads, conventionally <c>"//"</c>, is how a file carries one.
/// </para>
/// <para>
/// ⚠️ <b>Edits keep the file's order.</b> A key the file already has is replaced
/// where it stands; a new one goes at the end of its section, and a new section
/// at the end of the file.
/// </para>
/// </remarks>
public sealed class ConfigDocument
{
    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        NewLine = "\n",

        // A config file is read by people: "[BOT] " and "é" as typed, not \u escapes.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly JsonObject _root;

    private ConfigDocument(JsonObject root) => _root = root;

    /// <summary>An empty document.</summary>
    public ConfigDocument() : this(new JsonObject(NodeOptions)) { }

    /// <summary>
    /// Parses a file's text. Empty or whitespace-only text is an empty document.
    /// </summary>
    /// <exception cref="FormatException">
    /// Not JSON, not an object, or a name given twice. The message says where.
    /// </exception>
    public static ConfigDocument Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.AsSpan().Trim().IsEmpty)
            return new ConfigDocument();

        try
        {
            using var document = JsonDocument.Parse(text);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new FormatException("a config file is one JSON object, {\"Section\": {\"Key\": value}}");

            RefuseDuplicates(document.RootElement, string.Empty);
            return new ConfigDocument((JsonObject)JsonNode.Parse(text, NodeOptions)!);
        }
        catch (JsonException e)
        {
            var where = e.LineNumber is { } line ? $" at line {line + 1}" : string.Empty;
            throw new FormatException($"not valid JSON{where}: {e.Message}", e);
        }
    }

    /// <summary>The file's text: indented, LF line ends, one trailing newline.</summary>
    public string ToText() => _root.ToJsonString(WriteOptions) + "\n";

    /// <summary>A deep copy.</summary>
    public ConfigDocument Clone() => new((JsonObject)_root.DeepClone());

    /// <summary>The section names, in file order, that hold objects.</summary>
    public IEnumerable<string> Sections =>
        _root.Where(property => property.Value is JsonObject).Select(property => property.Key);

    /// <summary>Whether the file names <paramref name="key"/> with a non-null value.</summary>
    public bool Has(string section, string key) => Node(section, key) is not null;

    /// <summary>
    /// A key's value as text — the form <see cref="ServerSetting.Set"/> takes.
    /// </summary>
    /// <exception cref="FormatException">The section is not an object, or the value is.</exception>
    public bool TryGet(string section, string key, out string value)
    {
        var node = Node(section, key);
        value = node is null ? string.Empty : AsText(section + "." + key, node);
        return node is not null;
    }

    /// <summary>A key's text, or empty.</summary>
    public string Text(string section, string key) => TryGet(section, key, out var value) ? value : string.Empty;

    /// <summary>Every key in a section with its text, in file order. Null values are skipped.</summary>
    public IEnumerable<(string Key, string Value)> Entries(string section)
    {
        if (Section(section, create: false) is not { } entries)
            yield break;

        foreach (var (key, node) in entries)
        {
            if (node is not null)
                yield return (key, AsText(section + "." + key, node));
        }
    }

    /// <summary>Writes a setting's value, typed as the setting is.</summary>
    public void Set(ServerSetting setting, string value)
    {
        ArgumentNullException.ThrowIfNull(setting);
        Put(setting.Section, setting.Key, Typed(setting.Kind, value));
    }

    /// <summary>Writes a value as given, replacing the key in place or appending it.</summary>
    public void Put(string section, string key, JsonNode? value)
    {
        var entries = Section(section, create: true)!;

        // ⚠️ Found case-insensitively and replaced under the FILE's spelling, so
        // a save does not rename an operator's "servername" key.
        var existing = entries.FirstOrDefault(
            property => string.Equals(property.Key, key, StringComparison.OrdinalIgnoreCase)).Key;

        entries[existing ?? key] = value;
    }

    /// <summary>Removes a key. A section left empty stays, so its place in the file does too.</summary>
    public void Remove(string section, string key)
    {
        if (Section(section, create: false) is { } entries)
            entries.Remove(key);
    }

    /// <summary>Removes a whole section.</summary>
    public void RemoveSection(string section) => _root.Remove(section);

    /// <summary>
    /// <paramref name="overlay"/> over this document: every key it names wins,
    /// key by key, and everything else is this document's.
    /// </summary>
    /// <remarks>
    /// <b>Key by key, not section by section.</b> An overlay naming only
    /// <c>Server.ServerName</c> changes that one key and leaves the rest of
    /// <c>Server</c> alone. A <c>null</c> in the overlay is a key removed.
    /// </remarks>
    public ConfigDocument Merge(ConfigDocument overlay)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        var merged = Clone();

        foreach (var (name, node) in overlay._root)
        {
            if (node is JsonObject section && merged._root[name] is JsonObject)
            {
                foreach (var (key, value) in section)
                {
                    if (value is null)
                        merged.Remove(name, key);
                    else
                        merged.Put(name, key, value.DeepClone());
                }
            }
            else
            {
                merged._root[merged._root.FirstOrDefault(
                    p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Key ?? name] = node?.DeepClone();
            }
        }

        return merged;
    }

    /// <summary>Puts a top-level <c>"//"</c> note first in the file, replacing any.</summary>
    public void SetNote(string note)
    {
        var rest = _root.Where(p => p.Key != "//").Select(p => (p.Key, p.Value)).ToList();
        _root.Clear();
        _root["//"] = note;

        foreach (var (key, value) in rest)
            _root[key] = value;
    }

    /// <summary>
    /// The value a setting of <paramref name="kind"/> is written as.
    /// </summary>
    /// <remarks>
    /// Falls back to a string whenever the text is not the shape the kind
    /// suggests (<c>Speed</c> is a number or <c>max</c>), so writing never loses a
    /// value: the reader turns a string back into the same text.
    /// </remarks>
    public static JsonNode Typed(SettingKind kind, string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        switch (kind)
        {
            case SettingKind.Boolean:
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var flag))
                    return JsonValue.Create(flag != 0);
                break;

            case SettingKind.Integer:
                if (long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole))
                    return JsonValue.Create(whole);
                break;

            case SettingKind.Decimal:
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                    && double.IsFinite(number))
                {
                    return JsonValue.Create(number);
                }

                break;

            case SettingKind.List:
                return new JsonArray(value
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(entry => (JsonNode?)JsonValue.Create(entry))
                    .ToArray());
        }

        return JsonValue.Create(value);
    }

    /// <summary>A value as a setting's text: <c>true</c> is <c>1</c>, an array is comma-joined.</summary>
    private static string AsText(string id, JsonNode node)
    {
        switch (node)
        {
            case JsonArray array:
                return string.Join(',', array.Select((entry, i) => entry is null or JsonArray or JsonObject
                    ? throw new FormatException($"{id}: entry {i + 1} of the list must be a string or a number")
                    : Scalar(entry)));

            case JsonObject:
                throw new FormatException($"{id}: expected a value, got an object");

            default:
                return Scalar(node);
        }
    }

    private static string Scalar(JsonNode node) => node.GetValueKind() switch
    {
        JsonValueKind.String => node.GetValue<string>(),
        JsonValueKind.True => "1",
        JsonValueKind.False => "0",

        // The number exactly as written, so 10.0 reaches the setting as 10.0.
        _ => node.ToJsonString(),
    };

    private JsonObject? Section(string section, bool create)
    {
        var node = _root[section];

        if (node is JsonObject entries)
            return entries;

        if (node is not null)
            throw new FormatException($"{section}: a section must be an object, {{\"Key\": value}}");

        if (!create)
            return null;

        entries = new JsonObject(NodeOptions);
        _root[section] = entries;
        return entries;
    }

    private JsonNode? Node(string section, string key) => Section(section, create: false)?[key];

    private static void RefuseDuplicates(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                throw new FormatException($"\"{path}{property.Name}\" is named twice");

            // Sections and their keys; anything deeper is a value.
            if (path.Length == 0)
                RefuseDuplicates(property.Value, property.Name + ".");
        }
    }
}
