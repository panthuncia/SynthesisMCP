using System.Collections.Concurrent;
using System.Reflection;
using Loqui;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Plugins.Records;

namespace SafePatch.Mutagen;

/// <summary>
/// Which record types a record type can link to, read from the form links its generated getter declares
/// (<c>IFormLinkGetter&lt;T&gt;</c> anywhere in its fields, nested objects and lists, including every subclass of a
/// polymorphic field such as a condition's data), and whether it has asset links (model, texture or sound paths) at
/// all. A search for the records linking to one, or using a file, can skip every type that cannot. Child records (a
/// cell's placed objects) are records of their own and are not counted as their parent's.
/// </summary>
public static class LinkTypes
{
    private static readonly ConcurrentDictionary<Type, Targets> Cache = new();

    /// <param name="Any">A link whose target is not declared (or not known here), so any record might be its target.</param>
    /// <param name="Types">The declared target types (getter interfaces) of every other link.</param>
    /// <param name="Assets">Whether it has asset links, or links this walk cannot see into.</param>
    private sealed record Targets(bool Any, IReadOnlyList<Type> Types, bool Assets);

    /// <summary>Whether a record of <paramref name="sourceClass"/> can hold a link to a record of <paramref name="targetClass"/>.</summary>
    public static bool MayLinkTo(Type sourceClass, Type targetClass)
    {
        var targets = Cache.GetOrAdd(sourceClass, Find);
        if (targets.Any) return true;
        var target = LoquiRegistration.GetRegister(targetClass).GetterType;
        return targets.Types.Any(t => t.IsAssignableFrom(target));
    }

    /// <summary>Whether a record of <paramref name="sourceClass"/> can list an asset (a Data file) as a field does.</summary>
    public static bool MayHoldAssets(Type sourceClass) => Cache.GetOrAdd(sourceClass, Find).Assets;

    /// <summary>The types <paramref name="sourceClass"/>'s links declare, and whether one could be to any record.</summary>
    public static (bool Any, IReadOnlyList<Type> Types) Of(Type sourceClass)
    {
        var targets = Cache.GetOrAdd(sourceClass, Find);
        return (targets.Any, targets.Types);
    }

    private static Targets Find(Type classType)
    {
        var registration = LoquiRegistration.GetRegister(classType);
        var children = RecordDiff.ChildFields(classType);
        var found = new HashSet<Type>();
        var any = false;
        var assets = false;
        var seen = new HashSet<Type>();

        void Visit(Type type)
        {
            if (!seen.Add(type) || type == typeof(string) || type.IsPrimitive || type.IsEnum) return;
            if (typeof(IAssetLinkGetter).IsAssignableFrom(type))
            {
                assets = true;
                return;
            }
            var links = (type.IsInterface ? type.GetInterfaces().Prepend(type) : type.GetInterfaces())
                .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IFormLinkGetter<>))
                .Select(i => i.GetGenericArguments()[0])
                .ToList();
            if (links.Count > 0)
            {
                found.UnionWith(links);
                return;
            }
            if (typeof(IFormLinkGetter).IsAssignableFrom(type))
            {
                any = true; // an untyped link
                return;
            }
            // Another record held inside this one (a child) is searched as a record of its own.
            if (typeof(IMajorRecordGetter).IsAssignableFrom(type)) return;

            if (type.IsGenericType)
            {
                foreach (var argument in type.GetGenericArguments()) Visit(argument);
            }
            if (type.IsArray) Visit(type.GetElementType()!);
            foreach (var enumerable in type.GetInterfaces().Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>)))
                Visit(enumerable.GetGenericArguments()[0]);

            if (typeof(ILoquiObject).IsAssignableFrom(type) || IsLoquiGetter(type))
            {
                foreach (var property in Properties(type)) Visit(property.PropertyType);
                // A field of a base type can hold any of its subclasses, each with links of its own.
                foreach (var subclass in Subclasses(type)) Visit(subclass);
            }
            else if (type.IsInterface && typeof(IFormLinkContainerGetter).IsAssignableFrom(type))
            {
                // A link container of Mutagen's own (a link or an alias index): its links are its properties.
                foreach (var property in Properties(type)) Visit(property.PropertyType);
            }
            else if (typeof(IFormLinkContainerGetter).IsAssignableFrom(type) || typeof(IAssetLinkContainerGetter).IsAssignableFrom(type))
            {
                // Links this walk cannot see into.
                any |= typeof(IFormLinkContainerGetter).IsAssignableFrom(type);
                assets |= typeof(IAssetLinkContainerGetter).IsAssignableFrom(type);
            }
        }

        foreach (var property in Properties(registration.GetterType))
        {
            if (children.Contains(property.Name) || property.Name == nameof(IMajorRecordGetter.FormKey)) continue;
            Visit(property.PropertyType);
        }
        return new Targets(any, [.. found], assets);
    }

    private static bool IsLoquiGetter(Type type) =>
        type.IsInterface && !type.ContainsGenericParameters && LoquiRegistration.TryGetRegister(type, out _);

    private static IEnumerable<Type> Subclasses(Type type) =>
        type.IsInterface && SubclassMaps.GetOrAdd(type.Assembly, SubclassMap).TryGetValue(type, out var subclasses) ? subclasses : [];

    private static readonly ConcurrentDictionary<Assembly, IReadOnlyDictionary<Type, List<Type>>> SubclassMaps = new();

    /// <summary>Each getter interface of an assembly's generated classes, with the getter interfaces that extend it.</summary>
    private static IReadOnlyDictionary<Type, List<Type>> SubclassMap(Assembly assembly)
    {
        var map = new Dictionary<Type, List<Type>>();
        foreach (var getter in assembly.GetTypes().Where(t => t.IsInterface && IsLoquiGetter(t)))
        {
            foreach (var parent in getter.GetInterfaces().Where(i => i.Assembly == assembly && IsLoquiGetter(i)))
            {
                if (!map.TryGetValue(parent, out var list)) map[parent] = list = [];
                list.Add(getter);
            }
        }
        return map;
    }

    private static IEnumerable<PropertyInfo> Properties(Type type) =>
        type.IsInterface
            ? type.GetInterfaces().Prepend(type).SelectMany(i => i.GetProperties(BindingFlags.Public | BindingFlags.Instance)).DistinctBy(p => p.Name)
            : type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
}
