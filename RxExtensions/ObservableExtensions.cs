using System;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;

public static class ObservableExtensions
{
    public static IObservable<T> DelayedRefCount<T>(this IConnectableObservable<T> connectable, TimeSpan delay,
        IScheduler scheduler = null)
    {
        scheduler = scheduler ?? Scheduler.Default;

        var sync = new object();
        var count = 0;
        IDisposable connection = null;
        CancellationTokenSource disconnectionCancellationToken = null;

        return Observable.Create<T>(obs =>
        {
            lock (sync)
            {
                if (count++ == 0)
                {
                    // First subscriber; need to connect the connectable

                    // Special case: If the previous disconnection is still pending, do not connect again
                    if (disconnectionCancellationToken != null)
                    {
                        // In that case, cancel the disconnection
                        disconnectionCancellationToken.Cancel();
                    }
                    else
                    {
                        // TODO: Move out of the lock?
                        connection = connectable.Connect();
                    }
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
                        // Last subscriber; schedule a disconnect after a delay
                        ScheduleDisconnection();
                    }
                }
            }

            void ScheduleDisconnection()
            {
                // Note: This function is called within a lock(sync) block
                var cancellationToken = new CancellationTokenSource();
                var schedule = scheduler.Schedule(delay, DisconnectFromConnectable);
                cancellationToken.Token.Register(schedule.Dispose);

                // TODO: What if there's already a scheduled disconnection? Is that possible?
                disconnectionCancellationToken = cancellationToken;

                void DisconnectFromConnectable()
                {
                    lock (sync)
                    {
                        // Check the disconnectionCancellationToken hasn't changed; that would mean the
                        // current disconnect has been cancel right before this method was called
                        // ReSharper disable once AccessToModifiedClosure
                        if (ReferenceEquals(cancellationToken, disconnectionCancellationToken))
                        {
                            connection?.Dispose();
                            connection = null;
                        }
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