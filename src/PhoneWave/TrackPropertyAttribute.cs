using System;

namespace PhoneWave;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public class TrackPropertyAttribute : Attribute
{
    public TrackPropertyAttribute(Type type, string name)
    {
        
    }
}