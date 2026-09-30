using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;
using Loqui;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Strings;
using SafePatch.Host;
using SafePatch.Mutagen;

namespace SafePatch.Authoring.Query;

/// <summary>
/// A filter on record fields: clauses <c>Field op value</c> joined by <c>and</c>. Fields are Mutagen property paths
/// (<c>Value</c>, <c>Data.Damage</c>, <c>Entries.Count</c>). Operators: <c>== != &lt; &lt;= &gt; &gt;=</c>, <c>contains</c>
/// (text, flags, or a list element that is or links to the value) and <c>exists</c> (set and not empty). A value is
/// a FormKey, an EditorID, a number, an enum name or text (quoted when it has spaces).
/// </summary>
public sealed partial class Predicate
{
    private readonly IReadOnlyList<Clause> _clauses;
    private readonly Func<string, FormKey?> _editorIds;

    private Predicate(IReadOnlyList<Clause> clauses, Func<string, FormKey?> editorIds)
    {
        _clauses = clauses;
        _editorIds = editorIds;
    }

    public string Text { get; private init; } = "";

    private sealed record Clause(IReadOnlyList<string> Path, string Op, string Value);

    /// <param name="editorIds">Finds a record by EditorID, for values that name one.</param>
    public static Predicate Parse(string text, Func<string, FormKey?> editorIds)
    {
        var clauses = new List<Clause>();
        foreach (var part in AndSplit().Split(text.Trim()))
        {
            var match = ClauseSyntax().Match(part.Trim());
            if (!match.Success)
                throw new SafePatchException($"Cannot read the condition \"{part.Trim()}\". Write Field op value, e.g. Value > 100, Keywords contains ArmorHeavy, Name exists.");
            var value = match.Groups["value"].Value.Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
            var op = match.Groups["op"].Value.ToLowerInvariant();
            if (op != "exists" && value.Length == 0) throw new SafePatchException($"The condition \"{part.Trim()}\" has no value.");
            clauses.Add(new Clause(match.Groups["path"].Value.Split('.'), op, value));
        }
        return new Predicate(clauses, editorIds) { Text = text };
    }

    /// <summary>Checks the fields exist on a record type, so a typo fails before a scan rather than matching nothing.</summary>
    public void Validate(Type getter)
    {
        foreach (var clause in _clauses)
        {
            if (RecordText.FindProperty(getter, clause.Path[0]) is null)
            {
                var fields = LoquiRegistration.GetRegister(getter) is { } registration ? string.Join(", ", RecordDiff.FieldNames(registration.ClassType)) : "";
                throw new SafePatchException($"{Name(getter)} has no field {clause.Path[0]}. Its fields: {fields}.");
            }
        }
    }

    public bool Matches(IMajorRecordGetter record) => _clauses.All(c => Matches(record, c));

    private bool Matches(IMajorRecordGetter record, Clause clause)
    {
        object? value = record;
        foreach (var name in clause.Path)
        {
            if (value is null) return false;
            var property = RecordText.FindProperty(value, name);
            if (property is null) return false;
            value = property.GetValue(value);
        }

        if (clause.Op == "exists") return value is not null && (value is not IEnumerable items || value is string || items.Cast<object?>().Any());
        if (value is null) return clause.Op == "!=";
        return clause.Op switch
        {
            "contains" => Contains(value, clause.Value),
            "==" => Compare(value, clause.Value) == 0,
            "!=" => Compare(value, clause.Value) != 0,
            "<" => Compare(value, clause.Value) < 0,
            "<=" => Compare(value, clause.Value) <= 0,
            ">" => Compare(value, clause.Value) > 0,
            ">=" => Compare(value, clause.Value) >= 0,
            _ => false,
        };
    }

    private bool Contains(object value, string wanted)
    {
        switch (value)
        {
            case string text:
                return text.Contains(wanted, StringComparison.OrdinalIgnoreCase);
            case ITranslatedStringGetter translated:
                return translated.String?.Contains(wanted, StringComparison.OrdinalIgnoreCase) ?? false;
            case Enum flags:
                return Enum.TryParse(flags.GetType(), wanted, ignoreCase: true, out var flag) && flags.HasFlag((Enum)flag!);
            case IEnumerable items:
                var target = FormKeyOf(wanted);
                return items.Cast<object?>().Any(item => item is not null && (Compare(item, wanted) == 0
                    || (target is { } key && item is IFormLinkContainerGetter links && links.EnumerateFormLinks().Any(l => l.FormKey == key))
                    || (RecordText.Value(item)?.Contains(wanted, StringComparison.OrdinalIgnoreCase) ?? false)));
            default:
                return RecordText.Value(value)?.Contains(wanted, StringComparison.OrdinalIgnoreCase) ?? false;
        }
    }

    /// <summary>Compares a field value with the text of a condition; a mismatch of kinds compares unequal.</summary>
    private int Compare(object value, string wanted)
    {
        switch (value)
        {
            case IFormLinkGetter link:
                return FormKeyOf(wanted) is { } key && link.FormKey == key ? 0 : 1;
            case FormKey formKey:
                return FormKeyOf(wanted) is { } other && formKey == other ? 0 : 1;
            case Enum:
                return Enum.TryParse(value.GetType(), wanted, ignoreCase: true, out var parsed) ? Comparer.Default.Compare(Convert.ToInt64(value, CultureInfo.InvariantCulture), Convert.ToInt64(parsed, CultureInfo.InvariantCulture)) : 1;
            case bool flag:
                return bool.TryParse(wanted, out var other2) ? flag.CompareTo(other2) : 1;
            case IConvertible number when value.GetType().IsPrimitive || value is decimal:
                return double.TryParse(wanted, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? Convert.ToDouble(number, CultureInfo.InvariantCulture).CompareTo(n) : 1;
            case string text:
                return string.Compare(text, wanted, StringComparison.OrdinalIgnoreCase);
            case ITranslatedStringGetter translated:
                return string.Compare(translated.String, wanted, StringComparison.OrdinalIgnoreCase);
            case IEnumerable items when wanted.Equals("empty", StringComparison.OrdinalIgnoreCase):
                return items.Cast<object?>().Any() ? 1 : 0;
            default:
                return string.Equals(RecordText.Value(value), wanted, StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        }
    }

    /// <summary>A FormKey, or the record an EditorID names.</summary>
    private FormKey? FormKeyOf(string text) =>
        FormKey.TryFactory(text, out var formKey) ? formKey : _editorIds(text);

    private static string Name(Type getter) => getter.Name.StartsWith('I') && getter.Name.EndsWith("Getter") ? getter.Name[1..^6] : getter.Name;

    [GeneratedRegex(@"\s+and\s+", RegexOptions.IgnoreCase)]
    private static partial Regex AndSplit();

    [GeneratedRegex(@"^(?<path>[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*)\s*(?<op>==|!=|<=|>=|<|>|\bcontains\b|\bexists\b)\s*(?<value>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex ClauseSyntax();
}
