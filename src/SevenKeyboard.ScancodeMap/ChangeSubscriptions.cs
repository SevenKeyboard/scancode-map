using Subscription = SevenKeyboard.ScancodeMap.Subscription;

namespace SevenKeyboard;

// One external watch and one serialized callback queue, following the kana utilities' lifecycle.
internal sealed class ChangeSubscriptions(Func<Action, IRegistryWatch> startWatch) : IDisposable
{
    private readonly object _gate = new();
    private readonly List<Subscription> _subscriptions = [];
    private readonly Queue<Subscription[]> _batches = new();
    private WatchSession? _watch;
    private bool _draining;
    private bool _disposed;
    private int _dispatchThreadId;

    internal Subscription Subscribe(Func<bool> callback, bool prepend = false)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // A new subscription must not attach to a watch whose terminal observer is still queued.
            if (_watch?.Handle?.Completion.IsCompleted == true)
                EndWatch(_watch, WatchFailure(_watch.Handle.Completion));

            Subscription? previous = _subscriptions.Find(item => ReferenceEquals(item.Callback, callback));
            if (previous is not null)
                Detach(previous);
            Subscription subscription = new(this, callback);
            if (prepend)
                _subscriptions.Insert(0, subscription);
            else
                _subscriptions.Add(subscription);

            if (_watch is null)
            {
                WatchSession session = new();
                _watch = session;
                try
                {
                    session.Handle = startWatch(() => Changed(session));
                    _ = ObserveWatch(session);
                }
                catch (Exception exception)
                {
                    EndWatch(session, exception);
                }
            }
            return subscription;
        }
    }

    private async Task ObserveWatch(WatchSession session)
    {
        Exception failure;
        try
        {
            await session.Handle!.Completion.ConfigureAwait(false);
            failure = new IOException("Registry watch ended unexpectedly.");
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        lock (_gate)
            EndWatch(session, failure);
    }

    private static Exception WatchFailure(Task completion)
    {
        try { completion.GetAwaiter().GetResult(); }
        catch (Exception exception) { return exception; }
        return new IOException("Registry watch ended unexpectedly.");
    }

    private void EndWatch(WatchSession session, Exception failure)
    {
        if (!ReferenceEquals(_watch, session))
            return;
        _watch = null;
        foreach (Subscription subscription in _subscriptions.ToArray())
        {
            subscription.Failure ??= failure;
            Detach(subscription);
        }
    }

    private void Changed(WatchSession session)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_watch, session) || _disposed || _subscriptions.Count == 0)
                return;
            Subscription[] snapshot = _subscriptions.ToArray();
            foreach (Subscription subscription in snapshot)
                subscription.Pending++;
            _batches.Enqueue(snapshot);
            if (_draining)
                return;
            _draining = true;
            ThreadPool.QueueUserWorkItem(_ => Drain());
        }
    }

    private void Drain()
    {
        while (true)
        {
            Subscription[] batch;
            lock (_gate)
            {
                if (!_batches.TryDequeue(out batch!))
                {
                    _draining = false;
                    _dispatchThreadId = 0;
                    return;
                }
                _dispatchThreadId = Environment.CurrentManagedThreadId;
            }

            bool stopBatch = false;
            foreach (Subscription subscription in batch)
            {
                bool invoke;
                lock (_gate)
                    invoke = !stopBatch && subscription.Failure is null;
                Exception? failure = null;
                if (invoke)
                {
                    try { stopBatch = subscription.Callback(); }
                    catch (Exception exception) { failure = exception; }
                }

                IRegistryWatch? stop = null;
                lock (_gate)
                {
                    if (failure is not null)
                    {
                        subscription.Failure ??= failure;
                        Detach(subscription);
                        stop = TakeUnusedWatch();
                    }
                    subscription.Pending--;
                    subscription.Finish();
                }
                // The native callback only queues batches. Never join its thread while holding _gate.
                stop?.Dispose();
            }
        }
    }

    internal void Remove(Subscription subscription)
    {
        IRegistryWatch? stop;
        bool wait;
        lock (_gate)
        {
            Detach(subscription);
            stop = TakeUnusedWatch();
            wait = Environment.CurrentManagedThreadId != _dispatchThreadId;
        }
        stop?.Dispose();
        if (wait)
            subscription.Completion.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing).GetAwaiter().GetResult();
    }

    private void Detach(Subscription subscription)
    {
        _subscriptions.Remove(subscription);
        subscription.Detached = true;
        subscription.Finish();
    }

    private IRegistryWatch? TakeUnusedWatch()
    {
        if (_subscriptions.Count != 0)
            return null;
        IRegistryWatch? watch = _watch?.Handle;
        _watch = null;
        return watch;
    }

    public void Dispose()
    {
        IRegistryWatch? stop;
        lock (_gate)
        {
            _disposed = true;
            foreach (Subscription subscription in _subscriptions.ToArray())
                Detach(subscription);
            stop = TakeUnusedWatch();
        }
        stop?.Dispose();
    }

    private sealed class WatchSession
    {
        internal IRegistryWatch? Handle { get; set; }
    }
}
