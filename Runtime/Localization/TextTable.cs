using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexEngine.Localization;

/// <summary>A language a <see cref="TextTable"/> holds: its code (en, ru, zh) and its name in itself (Русский).</summary>
public sealed record Language(string Code, string Name);

/// <summary>
/// A game's text in every language it ships, found two ways.
/// <para>By key: <see cref="Get"/> and <see cref="Format"/> return a row's text in the current language, the source
/// language's when the current one lacks it, and the key itself when the table lacks the row.</para>
/// <para>By source text: <see cref="Translate"/> takes text written in the source language, as UI code that draws English
/// literals passes it, and returns the current language's text of the row whose source text it is (ignoring case and
/// keeping a leading indent). A row whose source text holds positional fields (<c>Level {0} of {1}</c>) also matches
/// text already formatted in the source language: the fields' values are read back out of it and put into the
/// translation, so counters and clocks need no format call of their own.</para>
/// <para>The pseudo locale (<see cref="Pseudo"/>) accents and brackets the source text, leaving fields, rich text tags
/// and line breaks alone, so that a capture shows any text that bypasses the table.</para>
/// <para>JSON form, as <see cref="Read"/> loads it: <c>{"Languages": [{"Code": "en", "Name": "English"}],
/// "Strings": {"key": {"en": "...", "ru": "..."}}}</c>. <see cref="Merge"/> adds a language's texts from a flat
/// <c>{"key": "text"}</c> object, so translations can live apart from a source table that a tool rewrites.</para>
/// </summary>
public sealed class TextTable
{
    public const string Pseudo = "qps";
    private sealed record Document(Language[] Languages, Dictionary<string, Dictionary<string, string>> Strings);

    private readonly List<Language> languages = [];
    private readonly Dictionary<string, Dictionary<string, string>> rows;
    private Dictionary<string, string> bySource;
    private (Regex Pattern, string Key)[] templates;

    /// <summary>The language every row is written in first and that fills in for a missing translation.</summary>
    public string Source { get; }
    public IReadOnlyList<Language> Languages => languages;
    public IEnumerable<string> Keys => rows.Keys;
    public string Current { get; private set; }
    /// <summary>Changes whenever the language or the text does, so cached layouts know to measure again.</summary>
    public int Revision { get; private set; }

    public TextTable(string source = "en", IEnumerable<Language> languages = null,
        Dictionary<string, Dictionary<string, string>> strings = null)
    {
        Source = Current = source;
        rows = strings ?? new(StringComparer.Ordinal);
        foreach (var language in languages ?? []) AddLanguage(language);
        if (this.languages.All(l => l.Code != source)) this.languages.Insert(0, new(source, source));
    }

    public static TextTable Read(ReadOnlySpan<byte> json, string source = "en")
    {
        var document = JsonSerializer.Deserialize<Document>(json);
        return new(source, document.Languages, new(document.Strings ?? [], StringComparer.Ordinal));
    }

    public void AddLanguage(Language language)
    {
        int at = languages.FindIndex(l => l.Code == language.Code);
        if (at >= 0) languages[at] = language; else languages.Add(language);
    }

    /// <summary>Sets one text; a source text replaces the row's translations' basis, so the source index is rebuilt.</summary>
    public void Set(string key, string language, string text)
    {
        if (!rows.TryGetValue(key, out var row)) rows[key] = row = new(StringComparer.Ordinal);
        row[language] = text;
        if (language == Source) { bySource = null; templates = null; }
        Revision++;
    }

    /// <summary>Adds a language's texts by key (rows the table lacks are created).</summary>
    public void Merge(Language language, IReadOnlyDictionary<string, string> texts)
    {
        AddLanguage(language);
        foreach (var (key, text) in texts) Set(key, language.Code, text);
    }

    /// <summary>A flat <c>{"key": "text"}</c> JSON object merged as one language.</summary>
    public void Merge(Language language, ReadOnlySpan<byte> json) =>
        Merge(language, JsonSerializer.Deserialize<Dictionary<string, string>>(json));

    public bool Supports(string code) => code == Pseudo || languages.Any(l => l.Code == code);

    /// <summary>Switches the language; an unknown code falls back to the source language.</summary>
    public void SetLanguage(string code)
    {
        if (!Supports(code)) code = Source;
        if (code == Current) return;
        Current = code;
        Revision++;
    }

    public bool Has(string key) => rows.ContainsKey(key);
    public bool Has(string key, string language) =>
        rows.TryGetValue(key, out var row) && row.TryGetValue(language, out var text) && !string.IsNullOrEmpty(text);

    /// <summary>The row's text in a language, without falling back (null when it has none).</summary>
    public string Text(string key, string language) =>
        rows.TryGetValue(key, out var row) && row.TryGetValue(language, out var text) && !string.IsNullOrEmpty(text) ? text : null;

    public string Get(string key)
    {
        if (key == null) return "";
        if (!rows.TryGetValue(key, out var row)) return key;
        string source = row.GetValueOrDefault(Source) ?? key;
        if (Current == Pseudo) return Pseudolocalize(source);
        return row.TryGetValue(Current, out var text) && text.Length > 0 ? text : source;
    }

    public string Format(string key, params object[] args) => string.Format(CultureInfo.InvariantCulture, Get(key), args);

    /// <summary>Text written in the source language, in the current one (see the class remarks); the text itself when
    /// no row has it.</summary>
    public string Translate(string text)
    {
        if (string.IsNullOrEmpty(text) || Current == Source) return text;
        if (Current == Pseudo) return Pseudolocalize(text);
        BuildSourceIndex();
        var body = text.TrimStart(' ');
        int indent = text.Length - body.Length;
        if (bySource.TryGetValue(body, out var key)) return new string(' ', indent) + Get(key);
        foreach (var (pattern, templateKey) in templates)
        {
            var match = pattern.Match(text);
            if (!match.Success) continue;
            var values = new object[match.Groups.Count - 1];
            for (int i = 1; i < match.Groups.Count; i++)
                values[int.Parse(match.Groups[i].Name[1..], CultureInfo.InvariantCulture)] = match.Groups[i].Value;
            try { return string.Format(CultureInfo.InvariantCulture, Get(templateKey), values); }
            catch (FormatException) { return text; }
        }
        return text;
    }

    private void BuildSourceIndex()
    {
        if (bySource != null) return;
        bySource = new(StringComparer.OrdinalIgnoreCase);
        var list = new List<(Regex, string)>();
        foreach (var (key, row) in rows)
        {
            if (!row.TryGetValue(Source, out var source) || string.IsNullOrWhiteSpace(source)) continue;
            bySource.TryAdd(source.TrimStart(' '), key);
            var fields = Placeholders(source);
            if (fields.Length == 0 || fields.Length != Regex.Matches(source, @"\{\d+(:[^}]*)?\}").Count) continue;
            // Each field read back lazily, the literal text between them exact.
            var pattern = new StringBuilder("^");
            int at = 0;
            foreach (Match m in Regex.Matches(source, @"\{(\d+)(:[^}]*)?\}"))
            {
                pattern.Append(Regex.Escape(source[at..m.Index])).Append("(?<f").Append(m.Groups[1].Value).Append(">.+?)");
                at = m.Index + m.Length;
            }
            pattern.Append(Regex.Escape(source[at..])).Append('$');
            list.Add((new Regex(pattern.ToString(), RegexOptions.CultureInvariant | RegexOptions.Singleline), key));
        }
        // Longer literal text first: the most specific template wins.
        templates = list.OrderByDescending(t => t.Item1.ToString().Length).ToArray();
    }

    /// <summary>The rows a language lacks (the source language's text shows in their place).</summary>
    public IEnumerable<string> Missing(string language) =>
        rows.Where(r => r.Value.TryGetValue(Source, out var s) && !string.IsNullOrEmpty(s) &&
                        (!r.Value.TryGetValue(language, out var t) || string.IsNullOrEmpty(t))).Select(r => r.Key);

    /// <summary>Every character a language's texts use, for baking or checking a font's glyphs.</summary>
    public SortedSet<char> Characters(string language)
    {
        var set = new SortedSet<char>();
        foreach (var row in rows.Values)
            if (row.TryGetValue(language, out var text)) foreach (char c in text) set.Add(c);
        return set;
    }

    /// <summary>Accents letters and brackets the text, leaving {n} fields, rich text tags and line breaks intact.</summary>
    public static string Pseudolocalize(string text)
    {
        const string plain = "AaEeIiOoUuCcNnYy", accented = "ÀàÉéÎîÖöÜüÇçÑñÝý";
        var sb = new StringBuilder(text.Length + 4).Append('[');
        int depth = 0;
        foreach (char c in text)
        {
            if (c is '{' or '<') depth++;
            if (depth == 0) { int i = plain.IndexOf(c); sb.Append(i >= 0 ? accented[i] : c); }
            else sb.Append(c);
            if (c is '}' or '>') depth = Math.Max(0, depth - 1);
        }
        return sb.Append(']').ToString();
    }

    /// <summary>The field indices ({0}, {1:00}) a format string uses, in order.</summary>
    public static int[] Placeholders(string text) => Regex.Matches(text, @"\{(\d+)(:[^}]*)?\}")
        .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).Distinct().Order().ToArray();
}
