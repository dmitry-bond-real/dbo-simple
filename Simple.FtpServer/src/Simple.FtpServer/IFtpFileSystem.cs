using System;
using System.Collections.Generic;
using System.IO;

namespace Simple.FtpServer
{
    public interface IFtpFileSystem
    {
        void Initialize(IEnumerable<FtpUserAccount> users);
        string NormalizePath(FtpUserAccount user, string workingDirectory, string argument);
        bool DirectoryExists(FtpUserAccount user, string virtualPath);
        bool FileExists(FtpUserAccount user, string virtualPath);
        IReadOnlyList<FtpFileEntry> GetEntries(FtpUserAccount user, string virtualPath);
        Stream OpenRead(FtpUserAccount user, string virtualPath);
        long GetLength(FtpUserAccount user, string virtualPath);
        DateTime GetLastWriteTimeUtc(FtpUserAccount user, string virtualPath);
        IFtpUploadTransaction BeginUpload(FtpUserAccount user, string virtualPath);
        void DeleteFile(FtpUserAccount user, string virtualPath);
        void CreateDirectory(FtpUserAccount user, string virtualPath);
        void DeleteDirectory(FtpUserAccount user, string virtualPath);
        void Move(FtpUserAccount user, string sourceVirtualPath, string destinationVirtualPath);
    }

    public interface IFtpUploadTransaction : IDisposable
    {
        Stream Stream { get; }
        void Commit();
    }

    public sealed class FtpFileEntry
    {
        public FtpFileEntry(string name, bool isDirectory, long length, DateTime lastWriteTimeUtc)
        {
            Name = name;
            IsDirectory = isDirectory;
            Length = length;
            LastWriteTimeUtc = lastWriteTimeUtc;
        }

        public string Name { get; private set; }
        public bool IsDirectory { get; private set; }
        public long Length { get; private set; }
        public DateTime LastWriteTimeUtc { get; private set; }
    }

    public sealed class FtpFileSystemConflictException : IOException
    {
        public FtpFileSystemConflictException() : base("The requested path is currently in use.")
        {
        }
    }

    public sealed class FtpPathUnavailableException : IOException
    {
        public FtpPathUnavailableException() : base("The requested path is unavailable.")
        {
        }
    }
}