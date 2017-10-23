using System;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;

public static class ObservableExtensions
{
    public static IObservable<T> DelayedRefCount<T>(this IConnectableObservable<T> connectable, TimeSpan delay)
    {
        var sync = new object();
        var count = 0;
        IDisposable connection = null;
        return Observable.Create<T>(obs =>
        {
            lock (sync)
            {
                if (count++ == 0)
                {
                    // First subscriber; need to connect the connectable
                    // TODO: Move out of the lock?
                    connection = connectable.Connect();
                }
            }

            var subscription = connectable.Subscribe(obs);

            void UnsubscribeObserver()
            {
                subscription.Dispose();
                lock (sync)
                {
                    if (--count == 0)
                    {
                        // Last subscriber
                        connection?.Dispose();
                        connection = null;
                    }
                }
            }

            return Disposable.Create(UnsubscribeObserver);
        });
    }

    public static IObservable<T> OnSubscribe<T>(this IObservable<T> src, Action action)
    {
        return Observable.Create<T>(obs =>
        {
            action();
            return src.Subscribe(obs);
        });
    }
    public static IObservable<T> OnUnsubscribed<T>(this IObservable<T> src, Action action)
    {
        return Observable.Create<T>(obs => new CompositeDisposable(
            src.Subscribe(obs),
            Disposable.Create(action)));
    }
}