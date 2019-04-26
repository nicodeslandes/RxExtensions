using System;
using System.Reactive.Linq;
using System.Threading;

namespace RxExtensions
{
    class Program
    {
        static void Main(string[] args)
        {
            TestDelayedRefCount();
            TestRateLimit();
        }

        static void TestRateLimit()
        {
            var src = Observable.Generate(1, v => v < 50, v => v + 1, v => v,
                v => TimeSpan.FromSeconds(v == 11 ? 3 : (v % 10) / 100.0));

            src.Subscribe(x => Console.WriteLine($"src: {x}"));

            src.RateLimit(TimeSpan.FromSeconds(1), true).Timestamp()
                .Subscribe(x => Console.WriteLine(x));
            Console.WriteLine("Press Enter to quit");
            Console.ReadLine();

        }

        static void TestDelayedRefCount()
        {
            var src = Observable.Interval(TimeSpan.FromSeconds(1))
                .OnSubscribe(() => Console.WriteLine("New subscriber"))
                .OnUnsubscribed(() => Console.WriteLine("Unsubscribed"))
                .Do(i => Console.WriteLine($"New item: {i}"));

            var refCounted = src
                .Publish()
                .DelayedRefCount(TimeSpan.FromSeconds(5));

            Console.WriteLine("Starting S1");
            var s1 = refCounted.Subscribe(i => Console.WriteLine($"S1: {i}"));

            Thread.Sleep(3000);
            Console.WriteLine("Starting S2");
            var s2 = refCounted.Subscribe(i => Console.WriteLine($"S2: {i}"));

            Thread.Sleep(4000);
            Console.WriteLine("Stopping S1");
            s1.Dispose();

            Thread.Sleep(4000);
            Console.WriteLine("Stopping S2");
            s2.Dispose();
            Console.WriteLine("S2 stopped");
            Thread.Sleep(4000);

            Console.WriteLine("Starting S3");
            var s3 = refCounted.Subscribe(i => Console.WriteLine($"S3: {i}"));
            Thread.Sleep(4000);
            Console.WriteLine("Stopping S3");
            s3.Dispose();
            Console.WriteLine("S3 stopped");
            Thread.Sleep(6000);

            var s4 = refCounted.Subscribe(i => Console.WriteLine($"S4: {i}"));
            Thread.Sleep(4000);
            s4.Dispose();
            Thread.Sleep(6000);

            Console.WriteLine("Press Enter to quit");
            Console.ReadLine();
        }
    }
}