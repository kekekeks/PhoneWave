# PhoneWave

This is a transaction-based changes tracker for your models.

# How to use

For simplest case of tracking property values, see the example project.

TL;DR:

Declaring your model classes:
```cs 
[TrackProperty(typeof(MyType), "MyTypePropertyName")]
public partial class MyModel : PhoneWaveBase
{ 
    public MyModel(PhoneWaveContext context) : base(context) {}
}
```
Making changes to your model:
```cs
using (var tran = myModel.Context.BeginTransaction("Changed MyTypePropertyName"))
{
    myModel.MyTypePropertyName = myNewValue;
    tran.Commit();
}
```
Actually undoing/redoing stuff:
```cs
if (myModel.Context.CanRollback)
    myModel.Context.Rollback();
    
if (myModel.Context.CanRollForward)
    myModel.Context.RollForward();
```

Whenever you're doing something wrong, you're getting an exception.

Also check out dictionary and collection tracking classes, you might find these useful too.
You will need read-only properties for these, constructed under the parent object's `PhoneWaveContext`.

GLHF.
