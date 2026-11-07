using System.Diagnostics;
using System.Text;
using Microsoft.Win32;
using SevenKeyboard;

internal static class OriginalAhk
{
    internal static void Run(RegistryKey root, string path, string interpreter, string devRoot)
    {
        string source = Path.Combine(devRoot, "scancode-map/main-ahkv1.1/ScancodeMap.ahk");
        string tempRoot = Path.GetFullPath(Path.GetTempPath());
        string temp = Path.GetFullPath(Path.Combine(tempRoot, "ScancodeMap-Ahk-" + Guid.NewGuid().ToString("N")));
        bool childExited = true;
        Directory.CreateDirectory(Path.Combine(temp, "lib"));
        var store = new RegistryStore(RegistryHive.CurrentUser, path, RegistryView.Registry64);
        // Write itself must create the previously missing path, just as original AHK RegWrite does.
        string[] fixtures =
        [
            "", "00", "FEFF00", "00000000", "0001020304050607",
            "07000000030000006300000000000000FEFFE0",
            "0000000000000000020000001D003AE000000000"
        ];
        byte[] fallback = Convert.FromHexString("AABBCCDD0102030405");
        if (!store.Write(fallback))
            throw new IOException("Could not initialize temporary AHK comparison value.");
        using RegistryKey key = root.OpenSubKey(path, writable: true)!;
        try
        {
            File.Copy(source, Path.Combine(temp, "ScancodeMap.ahk"));
            File.Copy(Path.Combine(devRoot, "event/main-ahkv1.1/Event.ahk"), Path.Combine(temp, "lib/Event.ahk"));
            File.Copy(Path.Combine(devRoot, "wait-for-single-object-async/main-ahkv1.1/WaitForSingleObjectAsync.ahk"),
                Path.Combine(temp, "lib/WaitForSingleObjectAsync.ahk"));
            File.WriteAllLines(Path.Combine(temp, "fixtures.txt"), fixtures.Select((hex, i) => i + "|" + hex),
                new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(temp, "Reference.ahk"), Script, new UTF8Encoding(true));

            var expected = new List<string>
            {
                "read|" + Convert.ToHexString(fallback),
                "omitted|" + Join(ReadChunks(store)),
                "empty|" + Join(ReadChunks(store, []))
            };
            for (int i = 0; i < fixtures.Length; i++)
                expected.Add("chunk" + i + "|" + Join(ReadChunks(store, Convert.FromHexString(fixtures[i]))));

            ProcessStartInfo start = new(interpreter)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                WorkingDirectory = temp
            };
            start.ArgumentList.Add("/ErrorStdOut");
            start.ArgumentList.Add(Path.Combine(temp, "Reference.ahk"));
            start.ArgumentList.Add(path);
            start.ArgumentList.Add(Path.Combine(temp, "fixtures.txt"));
            start.ArgumentList.Add(Path.Combine(temp, "results.txt"));
            using Process process = Process.Start(start) ?? throw new IOException("AHK did not start.");
            childExited = false;
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            // Always join the child before removing its script/includes, including timeout/error paths.
            if (!process.WaitForExit(30_000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                childExited = true;
                throw new TimeoutException("Original AHK comparison timed out.");
            }
            childExited = true;
            if (process.ExitCode != 0)
                throw new InvalidOperationException("AHK failed: " + output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult());

            // AHK writes each fixture into exactly the same temporary value; compare native C# reads.
            for (int i = 0; i < fixtures.Length; i++)
            {
                expected.Add("write" + i + "|1|" + fixtures[i] + "|" + Join(ScancodeMap.Split(Convert.FromHexString(fixtures[i]))));
            }
            expected.Add("delete|1");
            expected.Add("absent|0|");
            expected.Add("delete-again|0");
            expected.Add("create|1|FEFF000102");
            string[] actual = File.ReadAllLines(Path.Combine(temp, "results.txt"));
            if (!actual.SequenceEqual(expected))
                throw new InvalidOperationException("AHK comparison differs.\nExpected:\n" + string.Join("\n", expected)
                    + "\nActual:\n" + string.Join("\n", actual));
            if (!store.TryRead(out byte[]? deleted) || deleted is not null)
                throw new InvalidOperationException("AHK deletion was not visible to C#.");

            var ahkCreated = new RegistryStore(RegistryHive.CurrentUser, path + @"\CreatedByAhk\Nested", RegistryView.Registry64);
            if (!ahkCreated.TryRead(out byte[]? created) || created is null || Convert.ToHexString(created) != "FEFF000102")
                throw new InvalidOperationException("Original AHK did not create the missing key path.");

            // The AHK script saves each written REG_BINARY as a separate sibling value before deletion.
            for (int i = 0; i < fixtures.Length; i++)
            {
                if (key.GetValueKind("Fixture" + i) != RegistryValueKind.Binary ||
                    key.GetValue("Fixture" + i) is not byte[] bytes ||
                    !bytes.SequenceEqual(Convert.FromHexString(fixtures[i])))
                    throw new InvalidOperationException("AHK wrote different bytes for fixture " + i);
                key.SetValue("Scancode Map", bytes, RegistryValueKind.Binary);
                if (!store.TryRead(out byte[]? read) || read is null || !read.SequenceEqual(bytes))
                    throw new InvalidOperationException("C# did not read AHK bytes for fixture " + i);
            }
            Console.WriteLine("PASS: original AHK fixed hex chunks, empty/omitted fallback, same HKCU read/write/delete bytes.");
        }
        finally
        {
            // Never remove scripts if an exceptional process/kill path left exit unconfirmed.
            if (childExited)
            {
                if (!temp.StartsWith(Path.TrimEndingDirectorySeparator(tempRoot) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Unexpected temporary directory path.");
                Directory.Delete(temp, recursive: true);
            }
            else
                Console.Error.WriteLine("Child exit unconfirmed; retained AHK scripts at " + temp);
        }
    }

    private static byte[][] ReadChunks(RegistryStore store, byte[]? data = null)
    {
        if (!store.TryGetList(out byte[][] chunks, data))
            throw new IOException("Temporary comparison value could not be read.");
        return chunks;
    }

    private static string Join(byte[][] chunks) => string.Join(",", chunks.Select(chunk => Convert.ToHexString(chunk)));

    private const string Script = """
        #Requires AutoHotkey v1.1.36+
        #NoEnv
        #NoTrayIcon
        #SingleInstance Off
        SetBatchLines, -1
        #Include %A_ScriptDir%
        #Include .\ScancodeMap.ahk
        SetRegView, 64
        ScancodeMap._keyName := "HKEY_CURRENT_USER\" . A_Args[1]
        try {
            output := "read|" . ScancodeMap.read() . "`n"
            output .= "omitted|" . joinChunks(ScancodeMap.getList()) . "`n"
            output .= "empty|" . joinChunks(ScancodeMap.getList("")) . "`n"
            loop, Read, % A_Args[2]
            {
                parts := strSplit(A_LoopReadLine, "|")
                output .= "chunk" . parts[1] . "|" . joinChunks(ScancodeMap.getList(parts[2])) . "`n"
            }
            loop, Read, % A_Args[2]
            {
                parts := strSplit(A_LoopReadLine, "|")
                ok := ScancodeMap.write(parts[2])
                output .= "write" . parts[1] . "|" . ok . "|" . ScancodeMap.read() . "|" . joinChunks(ScancodeMap.getList()) . "`n"
                regWrite % "REG_BINARY", % ScancodeMap._keyName, % "Fixture" . parts[1], % ScancodeMap.read()
            }
            output .= "delete|" . ScancodeMap.delete() . "`n"
            output .= "absent|" . ScancodeMap.exist(value) . "|" . ScancodeMap.read() . "`n"
            output .= "delete-again|" . ScancodeMap.delete() . "`n"
            ScancodeMap._keyName .= "\CreatedByAhk\Nested"
            output .= "create|" . ScancodeMap.write("FEFF000102") . "|" . ScancodeMap.read() . "`n"
            fileAppend % output, % A_Args[3], UTF-8
            exitApp, 0
        } catch error {
            fileAppend % error.Message, *
            exitApp, 1
        }
        joinChunks(chunks) {
            result := ""
            for i,value in chunks
                result .= (i == 1 ? "" : ",") . value
            return result
        }
        """;
}
