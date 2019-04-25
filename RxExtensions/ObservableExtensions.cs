using System;
using System.Diagnostics;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;

namespace RxExtensions
{
    public static class ObservableExtensions
    {
        /// <summary>
        /// Implements a similar feature to <see cref="Observable.RefCount{TSource}(IConnectableObservable{TSource})"/>,
        /// but when the the subscriber count goes to 0, it delays the disconnection from the source connectable by
        /// a specified timespan.
        /// If any subscription occurs during that delay, the disconnection is cancelled.
        /// </summary>
        /// <typeparam name="T">The type of the elements in the source sequence</typeparam>
        /// <param name="source">Connectable observable sequence</param>
        /// <param name="delay">The time to wait after the last subscription is disposed of before we disconnect
        ///                     from the source</param>
        /// <param name="scheduler">The scheduler used to schedule disconnections</param>
        /// <returns>An observable sequence that stays connected to the source as long as there is
        ///          at least one subscription to the observable sequence and the disconnection delay hasn't expired.
        ///</returns>
        public static IObservable<T> DelayedRefCount<T>(this IConnectableObservable<T> source, TimeSpan delay,
            IScheduler scheduler = null)
        {
            scheduler = scheduler ?? Scheduler.Default;

            var sync = new object();
            var subscriberCount = 0;
            IDisposable connection = null;
            CancellationTokenSource disconnectionCancellationToken = null;

            return Observable.Create<T>(obs =>
            {
                lock (sync)
                {
                    if (subscriberCount++ == 0)
                    {
                        // First subscriber; need to connect the connectable source

                        // Special case: If a previous disconnection is still pending, cancel the pending
                        // disconnection schedule and do not connect again
                        if (disconnectionCancellationToken != null)
                        {
                            disconnectionCancellationToken.Cancel();
                            disconnectionCancellationToken = null;
                        }
                        else
                        {
                            // "Normal" connection: there's no pending disconnection
                            connection = source.Connect();
                        }
                    }
                }

                var subscription = source.Subscribe(obs);

                void UnsubscribeObserver()
                {
                    subscription.Dispose();
                    lock (sync)
                    {
                        if (--subscriberCount == 0)
                        {
                            // Last subscriber; schedule a disconnection after a delay
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

                    if (disconnectionCancellationToken != null)
                    {
                        // This should not happen: if we're scheduling a disconnection it means the last subscription
                        // was _just_ disposed. So there cannot be a pending disconnection at that point, because the
                        // subscriber count was greater than 0
                        Debug.Assert(false, "Invalid state: 2 simultaneous disconnections were found");

                        // If somehow it does happen, we should probably cancel the previous disconnection
                        disconnectionCancellationToken.Cancel();
                    }

                    disconnectionCancellationToken = cancellationToken;

                    void DisconnectFromConnectable()
                    {
                        lock (sync)
                        {
                            // Check the disconnectionCancellationToken hasn't changed; that would mean the
                            // current disconnect has been cancelled right before this method was called
                            // ReSharper disable once AccessToModifiedClosure
                            if (ReferenceEquals(cancellationToken, disconnectionCancellationToken))
                            {
                                // Dispose and reset the disconnectionCancellationToken so that a future connect can
                                // happen
                                disconnectionCancellationToken?.Dispose();
                                disconnectionCancellationToken = null;

                                // All subscribers have unsubscribed, and no-one resubcribed after the delay
                                // We can disconnect from the connectable observable
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

        public static IObservable<T> RateLimit<T>(this IObservable<T> src, TimeSpan minDelayBetweenItems,
            bool ensureLastItemsAreReplayed = false, IScheduler scheduler = null)
        {
            scheduler = scheduler ?? Scheduler.Default;

            return Observable.Create<T>(obs =>
            {
                var sync = new object();
                IDisposable currentSchedule = null;
                T savedItem = default;
                var lastIssuedTimestamp = DateTimeOffset.MinValue;
                return src.Subscribe(item =>
                {
                    var itemTimestamp = scheduler.Now;
                    if (itemTimestamp - lastIssuedTimestamp >= minDelayBetweenItems)
                    {
                        lock (sync)
                        {
                            lastIssuedTimestamp = itemTimestamp;
                            Console.WriteLine($"Item {item}: yielded");

                            if (currentSchedule != null)
                            {
                                Console.WriteLine($"Item {item}: cancelling timer (saved item: {savedItem})");
                                currentSchedule.Dispose();
                                currentSchedule = null;
                            }
                            obs.OnNext(item);
                        }
                    }
                    else if (ensureLastItemsAreReplayed)
                    {
                        lock (sync)
                        {
                            // We're skipping an item; if no item occurs after "minDelayBetweenItems" we have to
                            // yield it then
                            Console.WriteLine($"Item {item}: skipped, saving as item");
                            savedItem = item;
                            if (currentSchedule == null)
                            {
                                Console.WriteLine($"Item {item}: scheduling delayed yield");
                                currentSchedule = scheduler.Schedule(itemTimestamp + minDelayBetweenItems,
                                    () =>
                                    {
                                        lock (sync)
                                        {
                                            // Check the currentSchedule variable; it might have been cleared  in the
                                            // YieldPendingItemBeforeTermination function while we were
                                            // acquiring the lock
                                            if (currentSchedule != null)
                                            {
                                                currentSchedule = null;
                                                Console.WriteLine($"Item {savedItem}: issuing delayed item");
                                                lastIssuedTimestamp = scheduler.Now;
                                                obs.OnNext(savedItem);

                                                // Reset saved item to avoid a dangling reference
                                                savedItem = default;
                                            }
                                        }
                                    });
                            }
                        }
                    }
                },
                ex =>
                {
                    // If there is a pending item to be yielded, do it now before completing the observable
                    YieldPendingItemBeforeTermination();
                    obs.OnError(ex);
                },
                () =>
                {
                    // If there is a pending item to be yielded, do it now before completing the observable
                    YieldPendingItemBeforeTermination();
                    obs.OnCompleted();
                });

                void YieldPendingItemBeforeTermination()
                {
                    lock (sync)
                    {
                        if (currentSchedule != null)
                        {
                            currentSchedule.Dispose();
                            obs.OnNext(savedItem);
                        }
                    }
                }
            });
        }
    }
}