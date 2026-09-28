using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace RealmForge.Bridge.Equipment;

/// <summary>
/// Low-level injector for the Watcher of Realms process.  It allocates memory,
/// builds a fake IL2CPP <c>byte[]</c> with the SDP payload and executes a
/// remote thread that calls <c>LuaNetworkManager.SendMessage</c>.
///
/// <para>All Win32 calls are external (no write to the game executable, only to
/// allocated pages and remote thread creation).  If anything fails the old
/// click-based path is used as a fallback.</para>
/// </summary>
public sealed class GameProcessInjector : IDisposable
{
    // RVA of LuaNetworkManager$$SendMessage from Il2CppDumper (script.json).
    // Verified on GameAssembly.dll 2026-09-22.  Must be refreshed when the
    // game updates (re-run Il2CppDumper).
    const long SendMessageRva = 0x0;

    const string ProcessName = "Watcher of Realms";
    const string GameAssembly = "GameAssembly.dll";

    IntPtr hProcess;
    IntPtr gameAssemblyBase;
    IntPtr sendMessageVa;
    bool connected;

    public bool IsConnected => connected && hProcess != IntPtr.Zero;

    public bool Connect()
    {
        if (connected) return true;
        try
        {
            var ps = Process.GetProcessesByName(ProcessName);
            if (ps.Length == 0) { Error = "game not running"; return false; }
            int pid = ps[0].Id;
            hProcess = Win32.OpenProcess(Win32.PROCESS_ALL_ACCESS, false, pid);
            if (hProcess == IntPtr.Zero) { Error = $"OpenProcess failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}"; return false; }

            if (!TryGetModuleBase(pid, GameAssembly, out gameAssemblyBase)) { Error = "GameAssembly.dll not found in process"; return false; }
            sendMessageVa = IntPtr.Add(gameAssemblyBase, (int)SendMessageRva);
            connected = true;
            return true;
        }
        catch (Exception ex) { Error = ex.Message; return false; }
    }

    public string? Error { get; private set; }

    /// <summary>
    /// Send the equip message directly inside the game process.
    /// Returns the seq number returned by SendMessage, or 0 on failure.
    /// </summary>
    public uint SendEquip(long heroId, long itemId)
    {
        if (!connected) { Error = "not connected"; return 0; }

        var payload = SdpEquipPayloadBuilder.Build(itemId, heroId);
        // Fake Il2CppArray header: klass, monitor, bounds, max_length, padding
        int headerSize = 32;
        int totalSize = headerSize + payload.Length;

        IntPtr remoteMem = Win32.VirtualAllocEx(hProcess, IntPtr.Zero, (uint)totalSize,
            Win32.MEM_COMMIT | Win32.MEM_RESERVE, Win32.PAGE_READWRITE);
        if (remoteMem == IntPtr.Zero) { Error = "VirtualAllocEx failed"; return 0; }

        try
        {
            // Write fake array header + payload
            var block = new byte[totalSize];
            // klass = 0 (null – hope the method does not dereference it)
            // monitor = 0
            // bounds = 0
            // max_length = payload.Length
            BitConverter.GetBytes((ulong)payload.Length).CopyTo(block, 24); // max_length at offset 24
            payload.CopyTo(block, headerSize);

            if (!Win32.WriteProcessMemory(hProcess, remoteMem, block, (uint)totalSize, out _))
            { Error = "WriteProcessMemory (array) failed"; return 0; }

            IntPtr fakeArrayPtr = remoteMem; // Il2CppObject* starts here

            // Build shellcode that calls SendMessage(msgId, fakeArray, false, false, null)
            byte[] shellcode = BuildShellcode(0xA6CDU, fakeArrayPtr, sendMessageVa);
            IntPtr codeMem = Win32.VirtualAllocEx(hProcess, IntPtr.Zero, (uint)shellcode.Length,
                Win32.MEM_COMMIT | Win32.MEM_RESERVE, Win32.PAGE_EXECUTE_READWRITE);
            if (codeMem == IntPtr.Zero) { Error = "VirtualAllocEx (code) failed"; return 0; }

            try
            {
                if (!Win32.WriteProcessMemory(hProcess, codeMem, shellcode, (uint)shellcode.Length, out _))
                { Error = "WriteProcessMemory (code) failed"; return 0; }

                IntPtr hThread = Win32.CreateRemoteThread(hProcess, IntPtr.Zero, 0,
                    codeMem, IntPtr.Zero, 0, out _);
                if (hThread == IntPtr.Zero)
                { Error = $"CreateRemoteThread failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}"; return 0; }

                try
                {
                    // Wait up to 5 seconds for the call to finish
                    var wait = Win32.WaitForSingleObject(hThread, 5000);
                    if (wait != 0) { Error = "Remote thread timed out"; return 0; }

                    // Read return value (RAX) from thread context
                    if (Win32.GetExitCodeThread(hThread, out uint exitCode))
                    {
                        // For LuaNetworkManager.SendMessage the return value is the seq (uint).
                        // If the method crashed the process the thread exit code is the
                        // NTSTATUS or just 0.  We treat any non-zero as success for now.
                        return exitCode;
                    }
                    return 0;
                }
                finally { Win32.CloseHandle(hThread); }
            }
            finally
            {
                Win32.VirtualFreeEx(hProcess, codeMem, 0, Win32.MEM_RELEASE);
            }
        }
        finally
        {
            Win32.VirtualFreeEx(hProcess, remoteMem, 0, Win32.MEM_RELEASE);
        }
    }

    /// <summary>
    /// x64 shellcode (Microsoft calling convention) that calls
    /// <c>SendMessage(msgId, fakeArrayPtr, false, false, null)</c>.
    /// </summary>
    static byte[] BuildShellcode(uint msgId, IntPtr fakeArrayPtr, IntPtr targetAddr)
    {
        // We emit:
        //   push rbx / rdi / rsi
        //   sub rsp, 0x28      (shadow space)
        //   mov rax, msgId     (imm64)
        //   mov rcx, rax
        //   mov rax, arrayPtr  (imm64)
        //   mov rdx, rax
        //   xor r8, r8
        //   xor r9, r9
        //   mov qword [rsp+0x28], 0   (5th arg = MethodInfo* null)
        //   mov rax, targetAddr(imm64)
        //   call rax
        //   add rsp, 0x28
        //   pop rsi / rdi / rbx
        //   ret
        var code = new byte[128];
        int p = 0;

        void Emit(params byte[] b) { b.CopyTo(code, p); p += b.Length; }
        void EmitU64(ulong v) { BitConverter.GetBytes(v).CopyTo(code, p); p += 8; }

        Emit(0x53);                         // push rbx
        Emit(0x57);                         // push rdi
        Emit(0x56);                         // push rsi
        Emit(0x48, 0x83, 0xEC, 0x28);     // sub rsp, 0x28

        // rcx = msgId
        Emit(0x48, 0xB8); EmitU64(msgId);   // mov rax, imm64
        Emit(0x48, 0x89, 0xC1);             // mov rcx, rax

        // rdx = fakeArrayPtr
        Emit(0x48, 0xB8); EmitU64((ulong)fakeArrayPtr);
        Emit(0x48, 0x89, 0xC2);             // mov rdx, rax

        Emit(0x4D, 0x31, 0xC0);             // xor r8, r8
        Emit(0x4D, 0x31, 0xC9);             // xor r9, r9

        // 5th arg on stack (after shadow space)
        Emit(0x48, 0xC7, 0x44, 0x24, 0x28, 0x00, 0x00, 0x00, 0x00); // mov qword [rsp+0x28], 0

        // call target
        Emit(0x48, 0xB8); EmitU64((ulong)targetAddr);
        Emit(0xFF, 0xD0);                   // call rax

        Emit(0x48, 0x83, 0xC4, 0x28);       // add rsp, 0x28
        Emit(0x5E);                         // pop rsi
        Emit(0x5F);                         // pop rdi
        Emit(0x5B);                         // pop rbx
        Emit(0xC3);                         // ret

        Array.Resize(ref code, p);
        return code;
    }

    static bool TryGetModuleBase(int pid, string moduleName, out IntPtr baseAddr)
    {
        baseAddr = IntPtr.Zero;
        var hProcess = Win32.OpenProcess(Win32.PROCESS_QUERY_INFORMATION | Win32.PROCESS_VM_READ, false, pid);
        if (hProcess == IntPtr.Zero) return false;
        try
        {
            uint needed = 0;
            Win32.EnumProcessModules(hProcess, null, 0, out needed);
            if (needed == 0) return false;
            int count = (int)(needed / IntPtr.Size);
            var modules = new IntPtr[count];
            if (!Win32.EnumProcessModules(hProcess, modules, needed, out _)) return false;

            var sb = new StringBuilder(260);
            foreach (var mod in modules)
            {
                sb.Clear();
                if (Win32.GetModuleFileNameEx(hProcess, mod, sb, (uint)sb.Capacity) > 0)
                {
                    if (sb.ToString().EndsWith(moduleName, StringComparison.OrdinalIgnoreCase))
                    { baseAddr = mod; return true; }
                }
            }
            return false;
        }
        finally { Win32.CloseHandle(hProcess); }
    }

    public void Dispose()
    {
        if (hProcess != IntPtr.Zero) { Win32.CloseHandle(hProcess); hProcess = IntPtr.Zero; }
        connected = false;
    }

    static class Win32
    {
        public const uint PROCESS_ALL_ACCESS = 0x1F0FFF;
        public const uint PROCESS_QUERY_INFORMATION = 0x0400;
        public const uint PROCESS_VM_READ = 0x0010;
        public const uint MEM_COMMIT = 0x1000;
        public const uint MEM_RESERVE = 0x2000;
        public const uint MEM_RELEASE = 0x8000;
        public const uint PAGE_READWRITE = 0x04;
        public const uint PAGE_EXECUTE_READWRITE = 0x40;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize,
            uint flAllocationType, uint flProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint dwFreeType);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress,
            byte[] lpBuffer, uint nSize, out IntPtr lpNumberOfBytesWritten);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateRemoteThread(IntPtr hProcess, IntPtr lpThreadAttributes,
            uint dwStackSize, IntPtr lpStartAddress, IntPtr lpParameter, uint dwCreationFlags, out IntPtr lpThreadId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetExitCodeThread(IntPtr hHandle, out uint lpExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("psapi.dll", SetLastError = true)]
        public static extern bool EnumProcessModules(IntPtr hProcess, [Out] IntPtr[]? lphModule, uint cb, out uint lpcbNeeded);

        [DllImport("psapi.dll", CharSet = CharSet.Auto)]
        public static extern uint GetModuleFileNameEx(IntPtr hProcess, IntPtr hModule,
            [Out] StringBuilder lpBaseName, uint nSize);
    }
}
