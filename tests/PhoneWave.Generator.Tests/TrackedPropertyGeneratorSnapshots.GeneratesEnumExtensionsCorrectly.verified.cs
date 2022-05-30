//HintName: Test.Foo.g.cs

namespace Test
{
    partial class Foo
    {
        
PhoneWave.PhoneWavePropertyStorage<System.Collections.Generic.List<int>.Enumerator> __storageFor__Bar = new();
public System.Collections.Generic.List<int>.Enumerator Bar
{
    get => __storageFor__Bar.Value;
    set => SetValue(__storageFor__Bar, value);
}

        protected override void InitializeStorage()
        {
            __storageFor__Bar.Initialize(this, "Bar");

            base.InitializeStorage();
        }
    }
}