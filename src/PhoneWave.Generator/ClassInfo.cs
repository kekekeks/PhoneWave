using System;

namespace PhoneWave.Generator;

public readonly struct ClassInfo(string className, string classNamespace, (string PropertyType, string PropertyName)[] properties) : IEquatable<ClassInfo>
{
    public string ClassName { get; } = className;
    public string ClassNamespace { get; } = classNamespace;
    public (string PropertyType, string PropertyName)[] Properties { get; } = properties;

    static bool EqualProperties((string PropertyType, string PropertyName)[] a, (string PropertyType, string PropertyName)[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i].PropertyType != b[i].PropertyType || a[i].PropertyName != b[i].PropertyName) return false;
        }
        return true;
    }
        
    public bool Equals(ClassInfo other)
    {
        return string.Equals(ClassName, other.ClassName, StringComparison.InvariantCulture) 
               && string.Equals(ClassNamespace, other.ClassNamespace, StringComparison.InvariantCulture) 
               && EqualProperties(Properties, other.Properties);
    }

    public override bool Equals(object? obj) => obj is ClassInfo other && Equals(other);

    private static int PropertiesHashCode((string PropertyType, string PropertyName)[] properties)
    {
        var hash = 0;
        foreach (var (propertyType, propertyName) in properties)
        {
            hash = (hash * 397) ^ StringComparer.InvariantCulture.GetHashCode(propertyType);
            hash = (hash * 397) ^ StringComparer.InvariantCulture.GetHashCode(propertyName);
        }
        return hash;
    }
        
    public override int GetHashCode()
    {
        unchecked
        {
            var hashCode = StringComparer.InvariantCulture.GetHashCode(ClassName);
            hashCode = (hashCode * 397) ^ StringComparer.InvariantCulture.GetHashCode(ClassNamespace);
            hashCode = (hashCode * 397) ^ PropertiesHashCode(Properties);
            return hashCode;
        }
    }

    public static bool operator ==(ClassInfo left, ClassInfo right) => left.Equals(right);
    public static bool operator !=(ClassInfo left, ClassInfo right) => !left.Equals(right);
}