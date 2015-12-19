// 
// Copyright (c) 2004-2011 Jaroslaw Kowalski <jaak@jkowalski.net>
// 
// All rights reserved.
// 
// Redistribution and use in source and binary forms, with or without 
// modification, are permitted provided that the following conditions 
// are met:
// 
// * Redistributions of source code must retain the above copyright notice, 
//   this list of conditions and the following disclaimer. 
// 
// * Redistributions in binary form must reproduce the above copyright notice,
//   this list of conditions and the following disclaimer in the documentation
//   and/or other materials provided with the distribution. 
// 
// * Neither the name of Jaroslaw Kowalski nor the names of its 
//   contributors may be used to endorse or promote products derived from this
//   software without specific prior written permission. 
// 
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
// AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE 
// IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE 
// ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE 
// LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR 
// CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
// SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS 
// INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN 
// CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) 
// ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF 
// THE POSSIBILITY OF SUCH DAMAGE.
// 

using System.Security;

namespace NLog.Internal.FileAppenders
{
    using System;
    using System.IO;
    using System.Runtime.InteropServices;
    using NLog.Common;
    using NLog.Config;
    using NLog.Internal;
    using NLog.Time;

    /// <summary>
    /// Base class for optimized file appenders.
    /// </summary>
    [SecuritySafeCritical]
    internal abstract class BaseFileAppender : IDisposable
    {
        private readonly Random random = new Random();

        /// <summary>
        /// Initializes a new instance of the <see cref="BaseFileAppender" /> class.
        /// </summary>
        /// <param name="fileName">Name of the file.</param>
        /// <param name="createParameters">The create parameters.</param>
        public BaseFileAppender(string fileName, ICreateFileParameters createParameters)
        {
            this.CreateFileParameters = createParameters;
            this.FileName = fileName;
            this.OpenTime = DateTime.UtcNow; // to be consistent with timeToKill in FileTarget.AutoClosingTimerCallback
            this.LastWriteTime = DateTime.MinValue;
        }

        /// <summary>
        /// Gets the path of the file, including file extension.
        /// </summary>
        /// <value>The name of the file.</value>
        public string FileName { get; private set; }

        /// <summary>
        /// Gets the last write time.
        /// </summary>
        /// <value>The last write time. DateTime value must be of UTC kind.</value>
        public DateTime LastWriteTime { get; private set; }

        /// <summary>
        /// Gets the open time of the file.
        /// </summary>
        /// <value>The open time. DateTime value must be of UTC kind.</value>
        public DateTime OpenTime { get; private set; }

        /// <summary>
        /// Gets the file creation parameters.
        /// </summary>
        /// <value>The file creation parameters.</value>
        public ICreateFileParameters CreateFileParameters { get; private set; }

        /// <summary>
        /// Writes the specified bytes.
        /// </summary>
        /// <param name="bytes">The bytes.</param>
        public abstract void Write(byte[] bytes);

        /// <summary>
        /// Flushes this instance.
        /// </summary>
        public abstract void Flush();

        /// <summary>
        /// Closes this instance.
        /// </summary>
        public abstract void Close();

        /// <summary>
        /// Gets the file info.
        /// </summary>
        /// <param name="creationTime">The time the file was created. The value must be of UTC kind.</param>
        /// <param name="lastWriteTime">The last file write time. The value must be of UTC kind.</param>
        /// <param name="fileLength">Length of the file in bytes.</param>
        /// <returns>True if the operation succeeded, false otherwise.</returns>
        public abstract bool GetFileInfo(out DateTime creationTime, out DateTime lastWriteTime, out long fileLength);

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="disposing">True to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                this.Close();
            }
        }

        /// <summary>
        /// Records the last write time for a file.
        /// </summary>
        protected void FileTouched()
        {
            // always use system time in UTC to be consistent with FileInfo.LastWriteTimeUtc
            this.LastWriteTime = DateTime.UtcNow;
        }

        /// <summary>
        /// Records the last write time for a file to be specific date.
        /// </summary>
        /// <param name="dateTime">Date and time when the last write occurred. The value must be of UTC kind.</param>
        protected void FileTouched(DateTime dateTime)
        {
            this.LastWriteTime = dateTime;
        }

        /// <summary>
        /// Creates the file stream.
        /// </summary>
        /// <param name="allowFileSharedWriting">If set to <c>true</c> sets the file stream to allow shared writing.</param>
        /// <returns>A <see cref="FileStream"/> object which can be used to write to the file.</returns>
        protected FileStream CreateFileStream(bool allowFileSharedWriting)
        {
            int currentDelay = this.CreateFileParameters.ConcurrentWriteAttemptDelay;

            InternalLogger.Trace("Opening {0} with allowFileSharedWriting={1}", this.FileName, allowFileSharedWriting);
            for (int i = 0; i < this.CreateFileParameters.ConcurrentWriteAttempts; ++i)
            {
                try
                {
                    try
                    {
                        return this.TryCreateFileStream(allowFileSharedWriting);
                    }
                    catch (DirectoryNotFoundException)
                    {
                        if (!this.CreateFileParameters.CreateDirs)
                        {
                            throw;
                        }

                        Directory.CreateDirectory(Path.GetDirectoryName(this.FileName));
                        return this.TryCreateFileStream(allowFileSharedWriting);
                    }
                }
                catch (IOException)
                {
                    if (!this.CreateFileParameters.ConcurrentWrites || i + 1 == this.CreateFileParameters.ConcurrentWriteAttempts)
                    {
                        throw; // rethrow
                    }

                    int actualDelay = this.random.Next(currentDelay);
                    InternalLogger.Warn("Attempt #{0} to open {1} failed. Sleeping for {2}ms", i, this.FileName, actualDelay);
                    currentDelay *= 2;
                    System.Threading.Thread.Sleep(actualDelay);
                }
            }

            throw new InvalidOperationException("Should not be reached.");
        }

#if !SILVERLIGHT && !MONO && !__IOS__ && !__ANDROID__
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope", Justification = "Objects are disposed elsewhere")]
        private FileStream WindowsCreateFile(string fileName, bool allowFileSharedWriting)
        {
            int fileShare = Win32FileNativeMethods.FILE_SHARE_READ;

            if (allowFileSharedWriting)
            {
                fileShare |= Win32FileNativeMethods.FILE_SHARE_WRITE;
            }

            if (this.CreateFileParameters.EnableFileDelete && PlatformDetector.CurrentOS != RuntimeOS.Windows)
            {
                fileShare |= Win32FileNativeMethods.FILE_SHARE_DELETE;
            }

            Microsoft.Win32.SafeHandles.SafeFileHandle handle = null;
            FileStream fileStream = null;

            try
            {
                handle = Win32FileNativeMethods.CreateFile(
                fileName,
                Win32FileNativeMethods.FileAccess.GenericWrite,
                fileShare,
                IntPtr.Zero,
                Win32FileNativeMethods.CreationDisposition.OpenAlways,
                this.CreateFileParameters.FileAttributes,
                IntPtr.Zero);

                if (handle.IsInvalid)
                {
                    Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
                }

                fileStream = new FileStream(handle, FileAccess.Write, this.CreateFileParameters.BufferSize);
                fileStream.Seek(0, SeekOrigin.End);
                return fileStream;
            }
            catch
            {
                if (fileStream != null)
                    fileStream.Dispose();

                if ((handle != null) && (!handle.IsClosed))
                    handle.Close();

                throw;
            }
        }
#endif

        private FileStream TryCreateFileStream(bool allowFileSharedWriting)
        {
            FileShare fileShare = FileShare.Read;

            if (allowFileSharedWriting)
            {
                fileShare = FileShare.ReadWrite;
            }

            if (this.CreateFileParameters.EnableFileDelete && PlatformDetector.CurrentOS != RuntimeOS.Windows)
            {
                fileShare |= FileShare.Delete;
            }

#if !SILVERLIGHT && !MONO && !__IOS__ && !__ANDROID__
            try
            {
                if (!this.CreateFileParameters.ForceManaged && PlatformDetector.IsDesktopWin32)
                {
                    return this.WindowsCreateFile(this.FileName, allowFileSharedWriting);
                }
            }
            catch (SecurityException)
            {
                InternalLogger.Debug("Could not use native Windows create file, falling back to managed filestream");
            }
#endif

            return new FileStream(
                this.FileName,
                FileMode.Append,
                FileAccess.Write,
                fileShare,
                this.CreateFileParameters.BufferSize);
        }
    }
}