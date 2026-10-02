// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Glide contributors

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Glide;

internal static class ServiceNative
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void ServiceMain(uint count, nint args);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate uint Handler(uint control, uint type, nint data, nint context);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct ServiceEntry { public string? Name; public ServiceMain? Main; }
    [StructLayout(LayoutKind.Sequential)] internal struct Status { public uint Type, State, Accepted, Error, SpecificError, Checkpoint, WaitHint; }
    [StructLayout(LayoutKind.Sequential)] internal struct SecurityAttributes { public int Length; public nint Descriptor; public int Inherit; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct StartupInfo
    {
        public int Size; public string? Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XChars, YChars, Fill, Flags;
        public ushort Show, ReservedSize; public nint ReservedBytes, StdIn, StdOut, StdErr;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct ProcessInfo { public nint Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] internal struct JobBasic
    {
        public long ProcessTime, JobTime; public uint Flags; public nuint MinWorkingSet, MaxWorkingSet;
        public uint ActiveProcesses; public nuint Affinity; public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct JobLimits
    {
        public JobBasic Basic; public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool StartServiceCtrlDispatcher([In] ServiceEntry[] entries);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint RegisterServiceCtrlHandlerEx(string name, Handler handler, nint context);
    [DllImport("advapi32.dll", SetLastError = true)] internal static extern bool SetServiceStatus(nint handle, ref Status status);
    [DllImport("advapi32.dll", SetLastError = true)] internal static extern bool GetTokenInformation(nint token, int kind, nint data, int size, out int needed);
    [DllImport("advapi32.dll", SetLastError = true)] internal static extern bool DuplicateTokenEx(nint token, uint access, nint attributes, int impersonation, int type, out nint duplicate);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool CreateProcessAsUser(nint token, string application, StringBuilder command,
        nint processAttributes, nint threadAttributes, bool inherit, uint flags, nint environment, string directory, ref StartupInfo startup, out ProcessInfo process);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string text, uint revision, out nint descriptor, out uint size);
    [DllImport("wtsapi32.dll", SetLastError = true)] internal static extern bool WTSQueryUserToken(uint session, out nint token);
    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool WTSQuerySessionInformation(nint server, uint session, int informationClass, out nint buffer, out uint bytes);
    [DllImport("wtsapi32.dll")] internal static extern void WTSFreeMemory(nint memory);
    [DllImport("advapi32.dll", SetLastError = true)] internal static extern bool SetTokenInformation(nint token, int informationClass, ref uint value, uint length);
    [DllImport("kernel32.dll")] internal static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll")] internal static extern nint LocalFree(nint value);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint CreateEvent(ref SecurityAttributes attributes, bool manualReset, bool initial, string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint OpenEvent(uint access, bool inherit, string name);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool SetEvent(nint handle);
    [DllImport("kernel32.dll")] internal static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetExitCodeProcess(nint process, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint ResumeThread(nint thread);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool TerminateProcess(nint process, uint code);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint CreateJobObject(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool SetInformationJobObject(nint job, int kind, ref JobLimits limits, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool AssignProcessToJobObject(nint job, nint process);
    [DllImport("userenv.dll", SetLastError = true)] internal static extern bool CreateEnvironmentBlock(out nint environment, nint token, bool inherit);
    [DllImport("userenv.dll")] internal static extern bool DestroyEnvironmentBlock(nint environment);
    internal static void Check(bool success, string operation) { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error(), operation); }
    internal static unsafe T TokenValue<T>(nint token, int kind) where T : unmanaged
    {
        T value = default;
        Check(GetTokenInformation(token, kind, (nint)(&value), sizeof(T), out _), "Read user token");
        return value;
    }
}
