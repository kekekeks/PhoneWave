using System;
using PhoneWave;

namespace ConsoleTest
{
    class Program
    {
        static void Main(string[] args)
        {
            var ctx = new PhoneWaveContext();
            ctx.Resume();
            var data = new TestClass(ctx);
            data.PropertyChanged += (_, e) => Console.WriteLine("PropertyChanged: " + data.Foo);
            using (var t = ctx.BeginTransaction("Sup"))
            {
                data.Foo = 123;
                Console.WriteLine("Rolling back transaction");
            }

            Console.WriteLine("Value:" + data.Foo);

            using (var t = ctx.BeginTransaction("Commited"))
            {
                data.Foo = 123;
                t.Commit();
                Console.WriteLine("Commit");
            }
            Console.WriteLine("Value:" + data.Foo);

            Console.WriteLine("Rollback");
            ctx.Rollback();
            Console.WriteLine("Value:" + data.Foo);
            
            Console.WriteLine("RollForward");
            ctx.RollForward();
            Console.WriteLine("Value:" + data.Foo);
        }
    }
    
    [TrackProperty(typeof(int), "Foo")]
    partial class TestClass : PhoneWaveBase
    {
        public TestClass(PhoneWaveContext context) : base(context)
        {
        }
    }
}