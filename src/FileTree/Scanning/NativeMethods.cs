using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FileTree.Scanning;

internal static unsafe partial class NativeMethods
{
    public const uint GenericRead = 0x80000000;
    public const uint FileShareAll = 0x1 | 0x2 | 0x4;
    public const uint OpenExisting = 3;
    public const uint FileFlagNoBuffering = 0x20000000;
    public const uint FsctlGetNtfsVolumeData = 0x00090064;
    public const uint FileReadAttributes = 0x80;
    public const uint FileFlagBackupSemantics = 0x02000000;

    [StructLayout(LayoutKind.Sequential)]
    public struct NtfsVolumeData
    {
        public long VolumeSerialNumber;
        public long NumberSectors;
        public long TotalClusters;
        public long FreeClusters;
        public long TotalReserved;
        public uint BytesPerSector;
        public uint BytesPerCluster;
        public uint BytesPerFileRecordSegment;
        public uint ClustersPerFileRecordSegment;
        public long MftValidDataLength;
        public long MftStartLcn;
        public long Mft2StartLcn;
        public long MftZoneStart;
        public long MftZoneEnd;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public fixed uint Times[6]; // creation, last access, last write (FILETIME pairs)
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFile(string fileName, uint access, uint share, nint security, uint creation, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeviceIoControl(SafeFileHandle device, uint code, void* inBuffer, int inSize, void* outBuffer, int outSize, out int returned, nint overlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetDiskFreeSpace(string root, out uint sectorsPerCluster, out uint bytesPerSector, out uint freeClusters, out uint totalClusters);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation info);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    public static partial uint GetFinalPathNameByHandle(SafeFileHandle file, char* path, uint length, uint flags);

    public static string LastErrorMessage() => new Win32Exception(Marshal.GetLastPInvokeError()).Message;
}
