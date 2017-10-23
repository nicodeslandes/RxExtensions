using System;
using System.Reactive.Linq;
using System.Threading;

namespace RxExtensions
{
    class Program
    {
        static void Main(string[] args)
        {
            var src = Observable.Interval(TimeSpan.FromSeconds(1))
                .OnSubscribe(() => Console.WriteLine("New subscriber"))
                .OnUnsubscribed(() => Console.WriteLine("Unsubscribed"))
                .Do(i => Console.WriteLine($"New item: {i}"));

            var refCounted = src
                .Publish()
                .DelayedRefCount(TimeSpan.FromSeconds(5));
            //.RefCount();

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
            Thread.Sleep(4000);

            Console.WriteLine("Press Enter to quit");
            Console.ReadLine();
        }
    }
}