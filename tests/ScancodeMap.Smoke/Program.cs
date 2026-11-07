using System.Collections.Concurrent;
using Microsoft.Win32;
using SevenKeyboard;

internal static class Program
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static int Main(string[] args)
    {
        string path = @"Software\SevenKeyboard.Tests\ScancodeMap-" + Guid.NewGuid().ToString("N");
        using RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        Console.WriteLine($"Temporary key: HKCU\\{path}; process: {(Environment.Is64BitProcess ? "x64" : "x86")}");
        try
        {
            Data(root, path + @"\Data");
            if (args.Length == 2)
                OriginalAhk.Run(root, path + @"\Ahk", args[0], args[1]);
            else
                Console.WriteLine("AHK comparison omitted; use Run.ps1 to include it.");
            Ordering();
            CapturedBatch();
            CallbackFailures();
            WatchFailures();
            ReentrantAndDispose();
            Recovery(root, path + @"\Recovery");
            Churn(root, path + @"\Churn");
            Console.WriteLine("PASS: all ScancodeMap smoke tests.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            root.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
            using RegistryKey? remaining = root.OpenSubKey(path);
            Check(remaining is null, "Temporary key cleanup failed.");
            Console.WriteLine("Temporary HKCU subtree removed.");
        }
    }

    private static RegistryStore Store(string path) => new(RegistryHive.CurrentUser, path, RegistryView.Registry64);

    private static void Data(RegistryKey root, string path)
    {
        var store = Store(path);
        Check(store.TryRead(out byte[]? absent) && absent is null, "Missing key must succeed with null.");
        Check(store.TryGetList(out byte[][] absentChunks) && absentChunks.Length == 0, "Missing key list.");
        Check(!store.Delete(), "Missing key delete must fail.");
        using (RegistryKey? missing = root.OpenSubKey(path))
            Check(missing is null, "Read/list/delete created a missing key.");
        Check(store.Write([1, 2]), "Write must create a missing key path.");
        using RegistryKey key = root.OpenSubKey(path, writable: true)!;
        Check(key is not null, "Write did not create a key.");
        Check(store.TryRead(out byte[]? created) && created!.SequenceEqual(new byte[] { 1, 2 }), "Created value differs.");
        Check(store.Delete(), "Created value delete failed.");
        Check(store.TryRead(out absent) && absent is null && !store.Delete(), "Missing value semantics.");
        Check(store.TryGetList(out absentChunks, []) && absentChunks.Length == 0, "Missing value list.");
        // Deliberately malformed count/header, zero terminator, and a three-byte remainder.
        byte[] raw = [7, 0, 0, 0, 3, 0, 0, 0, 99, 0, 0, 0, 0, 0, 0, 0, 0xFE, 0xFF, 0xE0];
        Check(store.Write(raw), "Binary write failed.");
        Check(key!.GetValueKind("Scancode Map") == RegistryValueKind.Binary, "Wrong written type.");
        Check(store.TryRead(out byte[]? read) && read!.SequenceEqual(raw), "Raw bytes were changed.");
        foreach (byte[]? input in new byte[]?[] { null, [], raw })
        {
            Check(store.TryGetList(out byte[][] chunks, input), "Valid list read failed.");
            Check(chunks.Length == 5 && chunks[^1].Length == 3, "Raw chunk sizes.");
            Check(chunks.SelectMany(item => item).SequenceEqual(raw), "Header/terminator/remainder lost.");
        }
        Check(store.TryGetList(out byte[][] omitted) && omitted.SelectMany(item => item).SequenceEqual(raw), "Omitted fallback.");
        Check(ScancodeMap.TryGetList(out byte[][] copy, raw), "Explicit raw chunks failed.");
        copy[0][0] = 42;
        Check(raw[0] == 7, "Chunks alias input.");
        Check(ScancodeMap.TryGetList(out byte[][] shortChunks, [1, 2, 3]) &&
            shortChunks.Single().SequenceEqual(new byte[] { 1, 2, 3 }), "Short remainder.");
        Check(store.Write([]), "Zero-byte write failed.");
        Check(store.TryRead(out read) && read is { Length: 0 }, "Empty value confused with absence.");
        Check(store.TryGetList(out byte[][] empty) && empty.Length == 0 &&
            store.TryGetList(out empty, []) && empty.Length == 0, "Empty stored data.");
        foreach ((object value, RegistryValueKind kind) in new (object, RegistryValueKind)[]
        {
            ("text", RegistryValueKind.String), (17, RegistryValueKind.DWord),
            (new string[] { "one", "two" }, RegistryValueKind.MultiString), (new byte[] { 1 }, RegistryValueKind.None)
        })
        {
            key.SetValue("Scancode Map", value, kind);
            Check(!store.TryRead(out read) && read is null, "Wrong type must fail.");
            Check(!store.TryGetList(out empty) && empty.Length == 0, "List hid wrong type.");
            Check(!store.TryGetList(out empty, []) && empty.Length == 0, "Empty fallback hid wrong type.");
            Check(store.TryGetList(out byte[][] supplied, raw) && supplied.SelectMany(item => item).SequenceEqual(raw),
                "Explicit nonempty input must bypass registry errors.");
        }
        Check(store.Write(raw) && store.Delete() && !store.Delete(), "Delete failure semantics.");
        Check(store.TryRead(out read) && read is null, "Deleted value must be absent.");
        // Invalid root forces a real native read/write error without touching any other key.
        var invalid = new RegistryStore((RegistryHive)0, path, RegistryView.Registry64);
        Check(!invalid.TryRead(out read) && read is null && !invalid.Write([1]) && !invalid.Delete(), "Native error hidden.");
        Check(!invalid.TryGetList(out empty) && empty.Length == 0, "List hid native read failure.");
        Check(invalid.TryGetList(out byte[][] suppliedRaw, raw) && suppliedRaw.Length == 5, "Raw data tried to read registry.");
        bool nullThrows = false;
        try { store.Write(null!); }
        catch (ArgumentNullException) { nullThrows = true; }
        Check(nullThrows, "Null write must reject invalid input.");
        Console.WriteLine("PASS: binary preservation, raw chunks, null/empty fallback, write creates key, absence/type/native errors.");
    }

    private static void Ordering()
    {
        var factory = new FakeFactory();
        using var owner = new ChangeSubscriptions(factory.Start);
        var calls = new ConcurrentQueue<string>();
        Func<bool> first = () => { calls.Enqueue("A"); return false; };
        using var a = owner.Subscribe(first);
        using var b = owner.Subscribe(() => { calls.Enqueue("B"); return false; });
        using var c = owner.Subscribe(() => { calls.Enqueue("C"); return false; }, prepend: true);
        Check(calls.IsEmpty && factory.Starts == 1, "Initial callback or multiple watches.");
        using var replacement = owner.Subscribe(first);
        Await(a.Completion);
        a.Dispose(); // Must not remove the replacement.
        factory.Last!.Pulse();
        Until(() => calls.Count == 3);
        Check(string.Join("", calls) == "CBA", "Append/replacement ordering.");
        using var prepended = owner.Subscribe(first, prepend: true);
        Await(replacement.Completion);
        calls.Clear();
        factory.Last.Pulse();
        Until(() => calls.Count == 3);
        Check(string.Join("", calls) == "ACB", "Prepend ordering.");
        prepended.Dispose();
        b.Dispose();
        c.Dispose();
        Check(factory.Last.Stops == 1, "Last removal did not stop the watch.");

        calls.Clear();
        bool stop = true;
        using var gate = owner.Subscribe(() => { calls.Enqueue("S"); return stop; });
        using var tail = owner.Subscribe(() => { calls.Enqueue("T"); return false; });
        factory.Last.Pulse();
        gate.Dispose(); // Waits for its captured callback.
        tail.Dispose(); // Also waits for the skipped slot to drain.
        Check(string.Join("", calls) == "S", "True did not stop this batch.");

        int stopCalls = 0;
        using var repeatingStop = owner.Subscribe(() => { Interlocked.Increment(ref stopCalls); return true; });
        factory.Last.Pulse();
        Until(() => Volatile.Read(ref stopCalls) == 1);
        factory.Last.Pulse();
        Until(() => Volatile.Read(ref stopCalls) == 2);
        Check(!repeatingStop.Completion.IsCompleted, "True ended the subscriber.");
        repeatingStop.Dispose();

        var target = new Counter();
        Func<bool> sameMethod1 = new(target.Call);
        Func<bool> sameMethod2 = new(target.Call);
        Check(sameMethod1.Equals(sameMethod2) && !ReferenceEquals(sameMethod1, sameMethod2), "Delegate fixture.");
        using var identity1 = owner.Subscribe(sameMethod1);
        using var identity2 = owner.Subscribe(sameMethod2);
        factory.Last.Pulse();
        Until(() => Volatile.Read(ref target.Count) == 2);
        Console.WriteLine("PASS: shared watch, no initial callback, reference identity, order, replacement, true stops batch.");
    }

    private static void CapturedBatch()
    {
        var factory = new FakeFactory();
        using var owner = new ChangeSubscriptions(factory.Start);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var calls = new ConcurrentQueue<string>();
        using var blocker = owner.Subscribe(() =>
        {
            calls.Enqueue("A");
            entered.Set();
            Check(release.Wait(Timeout), "Captured batch release timed out.");
            return false;
        });
        Func<bool> callback = () => { calls.Enqueue("B"); return false; };
        using var old = owner.Subscribe(callback);
        factory.Last!.Pulse();
        Check(entered.Wait(Timeout), "Batch did not start.");
        using var replacement = owner.Subscribe(callback, prepend: true);
        Task removing = Task.Run(old.Dispose);
        try { Check(!removing.Wait(100), "Dispose did not wait for captured callback."); }
        finally { release.Set(); }
        Await(removing);
        Check(string.Join("", calls) == "AB", "Replacement changed a captured batch.");
        calls.Clear();
        factory.Last.Pulse();
        Until(() => calls.Count == 2);
        Check(string.Join("", calls) == "BA", "Replacement did not change the next batch.");
        Console.WriteLine("PASS: captured batches survive replacement; old Dispose waits without removing replacement.");
    }

    private static void CallbackFailures()
    {
        var factory = new FakeFactory();
        using var owner = new ChangeSubscriptions(factory.Start);
        var expected = new InvalidOperationException("Expected subscriber failure.");
        using var bad = owner.Subscribe(() => throw expected);
        int goodCalls = 0;
        using var good = owner.Subscribe(() => { Interlocked.Increment(ref goodCalls); return false; });
        factory.Last!.Pulse();
        Fault(bad.Completion, expected);
        Until(() => Volatile.Read(ref goodCalls) == 1);
        Check(factory.Last.Stops == 0 && !good.Completion.IsCompleted, "Failure ended another subscriber/watch.");
        factory.Last.Pulse();
        Until(() => Volatile.Read(ref goodCalls) == 2);
        good.Dispose();
        bad.Dispose();
        Check(factory.Last.Stops == 1, "Final removal after callback failure did not stop watch.");
        using var onlyBad = owner.Subscribe(() => throw expected);
        factory.Last.Pulse();
        Fault(onlyBad.Completion, expected);
        Until(() => factory.Last.Stops == 1);
        Console.WriteLine("PASS: callback exception faults only that subscriber, including final-subscriber cleanup.");
    }

    private static void WatchFailures()
    {
        var factory = new FakeFactory();
        var expected = new IOException("Expected watch failure.");
        factory.StartFailure = expected;
        using var owner = new ChangeSubscriptions(factory.Start);
        using var initial = owner.Subscribe(() => false);
        Fault(initial.Completion, expected);
        factory.StartFailure = null;
        using var first = owner.Subscribe(() => false);
        using var second = owner.Subscribe(() => false);
        FakeWatch previous = factory.Last!;
        previous.Fail(expected);
        // Immediately retry while the completion observer may still be queued.
        int calls = 0;
        using var retry = owner.Subscribe(() => { Interlocked.Increment(ref calls); return false; });
        Fault(first.Completion, expected);
        Fault(second.Completion, expected);
        Check(factory.Starts == 3 && !ReferenceEquals(previous, factory.Last), "Failed watch was not retried.");
        previous.Pulse();
        factory.Last!.Pulse();
        Until(() => Volatile.Read(ref calls) == 1);
        Check(!retry.Completion.IsCompleted, "Old completion ended a replacement watch.");
        Console.WriteLine("PASS: startup/terminal failure faults affected subscribers, later subscriptions retry.");
    }

    private static void ReentrantAndDispose()
    {
        var factory = new FakeFactory();
        using var owner = new ChangeSubscriptions(factory.Start);
        ScancodeMap.Subscription? self = null;
        ScancodeMap.Subscription? added = null;
        int calls = 0;
        self = owner.Subscribe(() =>
        {
            self!.Dispose();
            added = owner.Subscribe(() => { Interlocked.Increment(ref calls); return false; });
            return false;
        });
        factory.Last!.Pulse();
        Await(self.Completion);
        Until(() => added is not null);
        factory.Last!.Pulse();
        Until(() => Volatile.Read(ref calls) == 1);
        added!.Dispose();
        added.Dispose();

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int active = 0;
        int total = 0;
        using var serial = owner.Subscribe(() =>
        {
            Check(Interlocked.Increment(ref active) == 1, "Overlapping callbacks.");
            Interlocked.Increment(ref total);
            entered.Set();
            Check(release.Wait(Timeout), "Serial callback release timed out.");
            Interlocked.Decrement(ref active);
            return false;
        });
        factory.Last.Pulse();
        Check(entered.Wait(Timeout), "Serial callback did not start.");
        factory.Last.Pulse();
        Task removing = Task.Run(serial.Dispose);
        try { Check(!removing.Wait(100), "External Dispose returned before callback."); }
        finally { release.Set(); }
        Await(removing);
        Check(total == 2 && active == 0, "Captured second callback was lost or overlapped.");
        Console.WriteLine("PASS: self-disposal/reentrant subscribe, external disposal waits, serialized delivery.");
    }

    private static void Recovery(RegistryKey root, string path)
    {
        string target = path + @"\Parent\Target";
        RegistryStore store = Store(target);
        using var owner = new ChangeSubscriptions(store.Watch);
        int calls = 0;
        using var subscription = owner.Subscribe(() => { Interlocked.Increment(ref calls); return false; });
        Thread.Sleep(120);
        Check(calls == 0, "Missing key produced an initial callback.");
        using (RegistryKey? absent = root.OpenSubKey(path))
            Check(absent is null, "Watcher created a key.");
        using (root.CreateSubKey(path + @"\Parent")) { }
        Thread.Sleep(120);
        Check(calls == 0, "Ancestor creation invoked target callback.");
        using (root.CreateSubKey(target)) { }
        Until(() => Volatile.Read(ref calls) > 0);
        int before = Volatile.Read(ref calls);
        Check(store.Write([1, 2, 3]), "Write after creation failed.");
        Until(() => Volatile.Read(ref calls) > before);
        using (RegistryKey child = root.CreateSubKey(target + @"\Child"))
            child.SetValue("Unrelated", 1);
        Thread.Sleep(150); // Target NAME and descendant LAST_SET are excluded.
        before = Volatile.Read(ref calls);
        using (RegistryKey child = root.OpenSubKey(target + @"\Child", writable: true)!)
            child.SetValue("Unrelated", 2);
        Thread.Sleep(150);
        Check(Volatile.Read(ref calls) == before, "Watcher incorrectly watched target subtree.");
        root.DeleteSubKeyTree(path + @"\Parent");
        Until(() => Volatile.Read(ref calls) > before);
        Check(store.TryRead(out byte[]? absentData) && absentData is null, "Deleted key must be absent.");
        before = Volatile.Read(ref calls);
        using (root.CreateSubKey(target)) { }
        Until(() => Volatile.Read(ref calls) > before);
        before = Volatile.Read(ref calls);
        Check(store.Write([4, 5, 6]), "Write after recreation failed.");
        Until(() => Volatile.Read(ref calls) > before);
        subscription.Dispose();
        Await(subscription.Completion);
        before = calls;
        Check(store.Write([7]), "Post-disposal write failed.");
        Thread.Sleep(120);
        Check(calls == before, "Callback after Dispose returned.");
        using var retry = owner.Subscribe(() => { Interlocked.Increment(ref calls); return false; });
        Check(store.Delete(), "Delete existing value failed.");
        Until(() => Volatile.Read(ref calls) > before);
        Console.WriteLine("PASS: missing/created/deleted/recreated HKCU keys, no key creation, non-subtree watch, resubscribe.");
    }

    private static void Churn(RegistryKey root, string path)
    {
        var store = Store(path + @"\Parent\Target");
        using var owner = new ChangeSubscriptions(store.Watch);
        int calls = 0;
        using var subscription = owner.Subscribe(() => { Interlocked.Increment(ref calls); return false; });
        for (int i = 0; i < 40; i++)
        {
            using (RegistryKey key = root.CreateSubKey(path + @"\Parent\Target"))
                key.SetValue("Scancode Map", new byte[] { (byte)i }, RegistryValueKind.Binary);
            root.DeleteSubKeyTree(path);
        }
        using (root.CreateSubKey(path + @"\Parent\Target")) { }
        // Notifications coalesce; repeatedly write a final value until recovery is observed.
        int before = Volatile.Read(ref calls);
        Until(() =>
        {
            Check(store.Write([42]), "Final churn write failed.");
            return Volatile.Read(ref calls) > before;
        });
        Check(!subscription.Completion.IsCompleted, "Creation/deletion race terminated the watch.");
        Console.WriteLine("PASS: repeated creation/deletion races recover to the final HKCU key.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void Until(Func<bool> condition) =>
        Check(SpinWait.SpinUntil(condition, Timeout), "Condition timed out.");

    private static void Await(Task task) => task.WaitAsync(Timeout).GetAwaiter().GetResult();

    private static void Fault(Task task, Exception expected)
    {
        try { Await(task); }
        catch (Exception exception) when (ReferenceEquals(exception, expected)) { return; }
        throw new InvalidOperationException("Expected Completion failure was lost.");
    }

    private sealed class Counter
    {
        internal int Count;
        internal bool Call() { Interlocked.Increment(ref Count); return false; }
    }

    private sealed class FakeFactory
    {
        internal int Starts;
        internal Exception? StartFailure;
        internal FakeWatch? Last;

        internal IRegistryWatch Start(Action changed)
        {
            Starts++;
            if (StartFailure is not null)
                throw StartFailure;
            return Last = new FakeWatch(changed);
        }
    }

    private sealed class FakeWatch(Action changed) : IRegistryWatch
    {
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Stops;
        public Task Completion => _done.Task;
        internal void Pulse() => changed();
        internal void Fail(Exception error) => _done.TrySetException(error);
        public void Dispose()
        {
            Interlocked.Increment(ref Stops);
            _done.TrySetResult();
        }
    }
}
