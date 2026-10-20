using System.Collections;
using System.Collections.ObjectModel;
using System.Reflection;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     A JUSTIFIED opt-out from one of the reflection-driven field guards.
///     <para>Every entry carries a reason, and the guards assert that
///     <see cref="Property" /> still EXISTS on <see cref="Owner" /> — so renaming
///     or deleting the property breaks the exclusion loudly instead of silently
///     widening it into "and whatever replaced it".</para>
/// </summary>
/// <param name="Owner">The type declaring the excluded property.</param>
/// <param name="Property">Its exact property name.</param>
/// <param name="Reason">Why this property must not participate. "It fails the
/// test" is not a reason; "nothing transfers it, deliberately, because …" is.</param>
internal sealed record FieldExclusion(Type Owner, string Property, string Reason);

/// <summary>
///     The value generator behind the field-coverage guards.
///     <para>The problem it exists for: adding a field to a domain type means
///     hand-editing several parallel lists (<c>CopyFrom</c>, the
///     <c>ChartActionStateComparer</c> family, the rscp reader/writer pair), and
///     every omission fails SILENTLY — nothing warns, nothing fails to compile,
///     and the symptom shows up much later as a lost edit or a missing undo step.
///     A source lint that names fields would be one more parallel list; this is
///     reflection, so a new property is covered the day it compiles.</para>
///     <para><see cref="Populated(Type)" /> builds an instance whose every public
///     settable property differs from its type default; <see cref="Alternate" />
///     produces a second value distinguishable from the first, so a guard can
///     change exactly ONE property and ask whether the code under test noticed.</para>
/// </summary>
internal static class DomainFieldProbe
{
    /// <summary>Recursion stop for nested reference-typed properties. The domain
    /// graph has no cycles, so this only has to be deeper than the deepest real
    /// nesting (NotationVoice → Chords → ChordNotes → BendValues = 4).</summary>
    private const int MaxDepth = 8;

    /// <summary>The properties any copy/compare path could be expected to carry:
    /// public, instance, non-indexed, with a public setter.</summary>
    public static IReadOnlyList<PropertyInfo> SettableProperties(Type type)
    {
        return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0)
            .Where(p => p.GetSetMethod() is not null)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>An instance of <paramref name="type" /> with EVERY public settable
    /// property set to a value distinguishable from its default. Deterministic:
    /// two calls produce equal graphs.</summary>
    public static object Populated(Type type) => Populated(type, 0);

    /// <summary>Typed convenience over <see cref="Populated(Type)" />.</summary>
    public static T Populated<T>() where T : class => (T)Populated(typeof(T), 0);

    private static object Populated(Type type, int depth)
    {
        var instance = Activator.CreateInstance(type)
                       ?? throw new InvalidOperationException($"{type.Name} has no usable parameterless constructor.");

        if (depth >= MaxDepth) return instance;

        foreach (var prop in SettableProperties(type))
        {
            var value = Distinctive(prop.PropertyType, depth + 1, prop);
            prop.SetValue(instance, value);
        }

        return instance;
    }

    /// <summary>A value of <paramref name="type" /> that differs from its default.</summary>
    public static object? Distinctive(Type type, PropertyInfo? owner = null) => Distinctive(type, 1, owner);

    private static object? Distinctive(Type type, int depth, PropertyInfo? owner)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null) return Distinctive(underlying, depth, owner);

        if (type == typeof(bool)) return true;
        if (type == typeof(string)) return "dc-probe";
        if (type == typeof(char)) return 'z';
        if (type.IsEnum) return NonDefaultEnumValue(type);
        if (type == typeof(double)) return 3.25d;
        if (type == typeof(float)) return 3.25f;
        if (type == typeof(decimal)) return 3.25m;
        if (type == typeof(TimeSpan)) return TimeSpan.FromMilliseconds(1234);
        if (IsIntegral(type)) return Convert.ChangeType(7, type);

        if (type == typeof(Fraction)) return new Fraction(3, 7);
        if (type == typeof(FractionalPosition)) return new FractionalPosition(5, new Fraction(1, 3));

        if (type.IsArray)
        {
            var element = type.GetElementType()!;
            var array = Array.CreateInstance(element, 2);
            array.SetValue(Distinctive(element, depth + 1, owner), 0);
            array.SetValue(Distinctive(element, depth + 1, owner), 1);
            return array;
        }

        if (TryDictionaryArgs(type, out var keyType, out var valueType))
        {
            var dict = (IDictionary)Activator.CreateInstance(ConcreteDictionary(type, keyType, valueType))!;
            dict[Distinctive(keyType, depth + 1, owner)!] = Distinctive(valueType, depth + 1, owner);
            return dict;
        }

        if (TryCollectionArg(type, out var itemType))
        {
            var list = (IList)Activator.CreateInstance(ConcreteCollection(type, itemType))!;
            list.Add(Distinctive(itemType, depth + 1, owner));
            return list;
        }

        if (type.IsValueType) return Activator.CreateInstance(type);

        if (type.IsClass && !type.IsAbstract && type.GetConstructor(Type.EmptyTypes) is not null)
            return Populated(type, depth);

        throw new InvalidOperationException(
            $"DomainFieldProbe cannot build a distinctive value for {type.FullName}" +
            (owner is null ? "" : $" (needed for {owner.DeclaringType?.Name}.{owner.Name})") +
            ". Teach the generator about it — never let a property be skipped silently.");
    }

    /// <summary>A second value of <paramref name="type" />, distinguishable from
    /// <see cref="Distinctive(Type, PropertyInfo?)" />. Mostly the type default,
    /// which is what makes "mutate exactly one property" cheap.</summary>
    public static object? Alternate(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is not null) return null;

        if (type == typeof(bool)) return false;
        if (type == typeof(string)) return "";
        if (type == typeof(char)) return 'a';
        if (type.IsEnum) return Enum.ToObject(type, 0);
        if (type == typeof(double)) return 0d;
        if (type == typeof(float)) return 0f;
        if (type == typeof(decimal)) return 0m;
        if (type == typeof(TimeSpan)) return TimeSpan.Zero;
        if (IsIntegral(type)) return Convert.ChangeType(0, type);

        if (type == typeof(Fraction)) return new Fraction(0, 1);
        if (type == typeof(FractionalPosition)) return FractionalPosition.Zero;

        if (type.IsArray) return Array.CreateInstance(type.GetElementType()!, 0);

        if (TryDictionaryArgs(type, out var keyType, out var valueType))
            return Activator.CreateInstance(ConcreteDictionary(type, keyType, valueType));

        if (TryCollectionArg(type, out var itemType))
            return Activator.CreateInstance(ConcreteCollection(type, itemType));

        if (type.IsValueType) return Activator.CreateInstance(type);

        if (type.IsClass && !type.IsAbstract && type.GetConstructor(Type.EmptyTypes) is not null)
            return Activator.CreateInstance(type);

        throw new InvalidOperationException($"DomainFieldProbe cannot build an alternate value for {type.FullName}.");
    }

    private static object NonDefaultEnumValue(Type enumType)
    {
        var zero = Enum.ToObject(enumType, 0);
        foreach (var value in Enum.GetValues(enumType))
        {
            if (!value.Equals(zero))
                return value;
        }

        throw new InvalidOperationException(
            $"{enumType.Name} has only one member, so no mutation of it is observable.");
    }

    private static bool IsIntegral(Type type)
    {
        return type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort) ||
               type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong);
    }

    private static bool TryDictionaryArgs(Type type, out Type keyType, out Type valueType)
    {
        keyType = typeof(object);
        valueType = typeof(object);
        // IReadOnlyDictionary<,> does NOT implement IDictionary<,>, so a
        // read-only-typed property (LibraryArrangementEntry.Tones is the one that
        // found this) fell through to the throw instead of being generated.
        var definition = ClosedGeneric(type, typeof(IDictionary<,>))
                         ?? ClosedGeneric(type, typeof(IReadOnlyDictionary<,>));
        if (definition is null) return false;
        var args = definition.GetGenericArguments();
        keyType = args[0];
        valueType = args[1];
        return true;
    }

    private static bool TryCollectionArg(Type type, out Type itemType)
    {
        itemType = typeof(object);
        // Same gap as above: IReadOnlyList<>/IReadOnlyCollection<> do not
        // implement ICollection<>.
        var definition = ClosedGeneric(type, typeof(ICollection<>))
                         ?? ClosedGeneric(type, typeof(IReadOnlyCollection<>));
        if (definition is null) return false;
        itemType = definition.GetGenericArguments()[0];
        return true;
    }

    private static Type? ClosedGeneric(Type type, Type openInterface)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == openInterface) return type;
        return type.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == openInterface);
    }

    private static Type ConcreteDictionary(Type declared, Type keyType, Type valueType)
    {
        return declared.IsInterface || declared.IsAbstract
            ? typeof(Dictionary<,>).MakeGenericType(keyType, valueType)
            : declared;
    }

    private static Type ConcreteCollection(Type declared, Type itemType)
    {
        return declared.IsInterface || declared.IsAbstract
            ? typeof(ObservableCollection<>).MakeGenericType(itemType)
            : declared;
    }
}
