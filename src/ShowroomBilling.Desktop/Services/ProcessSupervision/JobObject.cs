using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ShowroomBilling.Desktop.Services.ProcessSupervision;

/// <summary>
/// Thin wrapper over a Windows Job Object configured with KillOnJobClose.
/// When this handle is released (normal exit OR crash), every process
/// assigned to the job is terminated by the OS — so spawned children
/// cannot outlive the Desktop.
/// </summary>
internal sealed class JobObject : IDisposable
{
    private IntPtr _handle;
    private bool _disposed;

    public JobObject()
    {
        _handle = CreateJobObject(IntPtr.Zero, null);
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException("CreateJobObject failed", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            }
        };

        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, fDeleteOld: false);
            if (!SetInformationJobObject(_handle, JobObjectInfoType.ExtendedLimitInformation, ptr, (uint)size))
                throw new InvalidOperationException("SetInformationJobObject failed", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    public void AssignProcess(Process process)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(JobObject));
        if (!AssignProcessToJobObject(_handle, process.Handle))
            throw new InvalidOperationException($"AssignProcessToJobObject failed for PID {process.Id}", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_handle != IntPtr.Zero)
        {
            CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    private enum JobObjectInfoType
    {
        ExtendedLimitInformation = 9,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr hJob, JobObjectInfoType infoType, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
