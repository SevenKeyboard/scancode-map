// ScancodeMap — Read, write and observe raw Windows Scancode Map data.
// GitHub: https://github.com/SevenKeyboard/scancode-map
// Author: SevenKeyboard Ltd. (2026)
// License: MIT License

using Microsoft.Win32;

namespace SevenKeyboard;

public static class ScancodeMap
{
    internal const string RegistryPath = @"SYSTEM\CurrentControlSet\Control\Keyboard Layout";
    private static readonly RegistryStore Store = new(RegistryHive.LocalMachine, RegistryPath, RegistryView.Default);
    private static readonly ChangeSubscriptions Shared = new(Store.Watch);

    static ScancodeMap() => AppDomain.CurrentDomain.ProcessExit += (_, _) => Shared.Dispose();

    /// <summary>Reads raw REG_BINARY data. Absence succeeds with null; read/type failure returns false and null.</summary>
    public static bool TryRead(out byte[]? data) => Store.TryRead(out data);

    /// <summary>Writes bytes unchanged, creating a missing key path as AHK RegWrite does. Returns false on failure.</summary>
    public static bool Write(byte[] data) => Store.Write(data);

    /// <summary>Deletes the value. Returns false on failure or a missing key/value.</summary>
    public static bool Delete() => Store.Delete();

    /// <summary>Splits raw data; null or empty input reads the registry, matching AHK.</summary>
    /// <remarks>Includes headers, terminators and a shorter remainder, without interpreting mappings.
    /// Absence succeeds with empty chunks; a read/type error returns false and empty chunks.</remarks>
    public static bool TryGetList(out byte[][] chunks, byte[]? data = null) => Store.TryGetList(out chunks, data);

    internal static byte[][] Split(byte[] data)
    {
        byte[][] chunks = new byte[data.Length / 4 + (data.Length % 4 == 0 ? 0 : 1)][];
        for (int i = 0; i < chunks.Length; i++)
            chunks[i] = data.AsSpan(i * 4, Math.Min(4, data.Length - i * 4)).ToArray();
        return chunks;
    }

    /// <summary>Subscribes to key value changes, without an initial callback.</summary>
    /// <remarks>
    /// All callbacks share one serialized background queue. Returning true stops only the current batch.
    /// The same delegate object replaces and repositions its subscription; distinct delegate objects stay distinct.
    /// Captured batches keep their order after removal/replacement. Old handles cannot remove replacements.
    /// A throwing callback ends only its subscriber. Startup/terminal watch failures fault affected Completions;
    /// later subscriptions retry. The last removal stops the watch. Missing keys are awaited without creation.
    /// </remarks>
    public static Subscription Subscribe(Func<bool> callback, bool prepend = false) => Shared.Subscribe(callback, prepend);

    public sealed class Subscription : IDisposable
    {
        private readonly ChangeSubscriptions _owner;
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Subscription(ChangeSubscriptions owner, Func<bool> callback)
        {
            _owner = owner;
            Callback = callback;
        }

        internal Func<bool> Callback { get; }
        internal int Pending { get; set; }
        internal bool Detached { get; set; }
        internal Exception? Failure { get; set; }

        /// <summary>Completes after removal and captured callbacks; faults on callback or watch failure.</summary>
        /// <remarks>Do not wait for this task from a callback on the shared queue.</remarks>
        public Task Completion => _completion.Task;

        internal void Finish()
        {
            if (!Detached || Pending != 0)
                return;
            if (Failure is null)
                _completion.TrySetResult();
            else
                _completion.TrySetException(Failure);
        }

        /// <summary>Removes this handle and waits for captured callbacks, except from the callback queue.</summary>
        /// <remarks>Repeatable; faults remain on Completion. Do not block a callback on a thread calling Dispose.</remarks>
        public void Dispose() => _owner.Remove(this);
    }
}
