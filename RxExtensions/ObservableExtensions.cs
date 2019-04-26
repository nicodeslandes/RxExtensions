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

        /// <summary>
        /// This operator ensures that an observable only produces items under a given rate, expressed as a minimum delay
        /// between 2 consecutive items.
        /// </summary>
        /// <remarks>
        /// 2 implementations are used depending on the <paramref name="ensureYieldAfterLongDelay"/> parameter.
        /// if this is <c>true</c>, if no item is produced for a delay longer than
        /// <paramref name="minDelayBetweenItems"/>, then the item before that delay is guaranteed to be yielded (after
        /// potentially a delay to ensure <paramref name="minDelayBetweenItems"/> is respected).
        /// If <paramref name="ensureYieldAfterLongDelay"/> is <c>false</c>, then no such guarantee is made, but the
        /// implementation is much more efficient: it requires no per-item allocation, and it's lock-free. It is
        /// therefore the default, and is recommended in case of high throughput from <paramref name="src"/>.
        /// </remarks>
        /// <typeparam name="T">Type of the source items</typeparam>
        /// <param name="src">Source observable</param>
        /// <param name="minDelayBetweenItems">The minimum delay to be enforced between items of the
        ///                                    resulting observable</param>
        /// <param name="ensureYieldAfterLongDelay">If set to <c>true</c>, any item that isn't followed by another item
        ///                                          within <paramref name="minDelayBetweenItems"/> is guaranteed to be
        ///                                          yielded. Warning, this requires a much more costly implementation
        ///                                          and therefore shouldn't be used for observables with a
        ///                                          high throughput</param>
        /// <param name="scheduler">Scheduler to use</param>
        /// <returns>An observable guaranteed to only issue items separated by at
        ///          least <paramref name="minDelayBetweenItems"/></returns>
        public static IObservable<T> RateLimit<T>(this IObservable<T> src, TimeSpan minDelayBetweenItems,
            bool ensureYieldAfterLongDelay = false, IScheduler scheduler = null)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (minDelayBetweenItems <= TimeSpan.Zero)
                throw new ArgumentException("Invalid value", nameof(minDelayBetweenItems));

            scheduler = scheduler ?? Scheduler.Default;

            if (!ensureYieldAfterLongDelay)
            {
                // Simple implementation in case there's no need to ensure last items before a long pause are yielded
                // if they haven't been yielded yet
                return Observable.Create<T>(obs =>
                {
                    var lastIssuedTimestamp = DateTimeOffset.MinValue;
                    return src.Subscribe(item =>
                    {
                        var itemTimestamp = scheduler.Now;
                        if (itemTimestamp - lastIssuedTimestamp >= minDelayBetweenItems)
                        {
                            lastIssuedTimestamp = itemTimestamp;
                            obs.OnNext(item);
                        }
                    },
                    obs.OnError, obs.OnCompleted);
                });
            }

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
                            Debug.WriteLine($"Item {item}: yielding item");
                            YieldItem(item);
                        }
                    }
                    else
                    {
                        lock (sync)
                        {
                            // We're skipping an item; if no item occurs after "minDelayBetweenItems" we have to
                            // yield it then
                            Debug.WriteLine($"Item {item}: skipped, saving as delayed item");
                            savedItem = item;
                            if (currentSchedule == null)
                            {
                                Debug.WriteLine($"Item {item}: scheduling delayed yield");
                                var newSchedule = new SingleAssignmentDisposable();
                                newSchedule.Disposable = scheduler.Schedule(lastIssuedTimestamp + minDelayBetweenItems,
                                    () => YieldPendingItem(newSchedule));
                                currentSchedule = newSchedule;
                            }
                        }
                    }
                },
                obs.OnError,
                () =>
                {
                    // If there is a pending item to be yielded, yield it now before completing the observable
                    lock (sync)
                    {
                        if (currentSchedule != null)
                        {
                            YieldItem(savedItem);
                        }
                    }

                    obs.OnCompleted();
                });

                void YieldPendingItem(IDisposable schedule)
                {
                    lock (sync)
                    {
                        // Check the currentSchedule variable to see if it's changed since this function was scheduled
                        // if it has, it means any pending item was already yielded; in that case there's nothing to do
                        if (currentSchedule == schedule)
                        {
                            Debug.WriteLine($"Item {savedItem}: issuing delayed item");
                            YieldItem(savedItem);
                        }
                    }
                }

                void YieldItem(T item)
                {
                    // Note: This function is to always be called under a lock

                    // Cancel any pending schedule
                    currentSchedule?.Dispose();
                    currentSchedule = null;

                    lastIssuedTimestamp = scheduler.Now;
                    obs.OnNext(item);

                    // Reset saved item to avoid a dangling reference
                    savedItem = default;
                }

            });
        }
    }
}