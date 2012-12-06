﻿using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.IO;
using System.Threading;

namespace CannedBytes.Midi
{
    /// <summary>
    /// The MidiBufferManager base class implements unmanaged memory management
    /// for <see cref="MidiBufferStream"/>s that are used by the <see cref="MidiPort"/>s
    /// for sending and receiving sysex message or sending streams of midi events.
    /// </summary>
    public abstract class MidiBufferManager : UnmanagedDisposableBase
    {
        private IntPtr memHeaders = IntPtr.Zero;
        private IntPtr memBuffers = IntPtr.Zero;
        private readonly object locker = new object();
        private readonly List<MidiBufferStream> usedBuffers = new List<MidiBufferStream>();
        private readonly Queue<MidiBufferStream> unusedBuffers = new Queue<MidiBufferStream>();
        private readonly Dictionary<IntPtr, MidiBufferStream> mapBuffers = new Dictionary<IntPtr, MidiBufferStream>();
        private readonly AutoResetEvent buffersReturnedEvent = new AutoResetEvent(false);

        /// <summary>
        /// For derived classes only.
        /// </summary>
        /// <param name="port">A reference to the midi port this buffer manager serves.</param>
        /// <param name="access">The type of access the stream provides to the underlying buffer.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="port"/> is null.</exception>
        internal MidiBufferManager(MidiPort port, FileAccess access)
        {
            Contract.Requires(port != null);

            Throw.IfArgumentNull(port, "port");

            MidiPort = port;
            StreamAccess = access;
        }

        [ContractInvariantMethod]
        private void InvariantContract()
        {
            Contract.Invariant(this.unusedBuffers != null);
            Contract.Invariant(this.usedBuffers != null);
            Contract.Invariant(this.mapBuffers != null);
            Contract.Invariant(this.buffersReturnedEvent != null);
            Contract.Invariant(this.locker != null);
        }

        private MidiPort midiPort;

        /// <summary>
        /// Gets the MidiPort this buffer manager serves.
        /// </summary>
        protected MidiPort MidiPort
        {
            get
            {
                Contract.Ensures(this.midiPort != null);

                return this.midiPort;
            }
            set
            {
                Contract.Requires(value != null);
                Throw.IfArgumentNull(value, "MidiPort");

                this.midiPort = value;
            }
        }

        /// <summary>
        /// Gets the type of access the stream provides to the underlying data.
        /// </summary>
        public FileAccess StreamAccess { get; private set; }

        /// <summary>
        /// Gets the size (in bytes) of each buffer.
        /// </summary>
        public int BufferSize { get; private set; }

        /// <summary>
        /// Gets the number of buffers that will be available.
        /// </summary>
        public int BufferCount { get; private set; }

        /// <summary>
        /// Gets the number of buffers currently in use by the <see cref="MidiPort"/>.
        /// </summary>
        public int UsedBufferCount
        {
            get { ThrowIfDisposed(); return this.usedBuffers.Count; }
        }

        /// <summary>
        /// Gets the number of buffers that are currently not in use by the <see cref="MidiPort"/>.
        /// </summary>
        public int UnusedBufferCount
        {
            get { ThrowIfDisposed(); return this.unusedBuffers.Count; }
        }

        /// <summary>
        /// Indicates if the buffer manager has been initialized.
        /// </summary>
        /// <remarks>Call the <see cref="M:Initialze"/> method to initialize this instance.</remarks>
        public bool IsInitialized
        {
            get { return ((this.memHeaders != IntPtr.Zero || this.memBuffers != IntPtr.Zero)); }
        }

        /// <summary>
        /// Blocks the calling thread until all buffers are returned
        /// to the manager or the specified timeout period has elapsed.
        /// </summary>
        /// <param name="millisecondTimeout">A timeout in milliseconds.</param>
        /// <returns>Returns true if all buffers were returned or false when the timeout expired.</returns>
        public bool WaitForBuffersReturned(int millisecondTimeout)
        {
            ThrowIfDisposed();

            if (UsedBufferCount == 0)
            {
                return true;
            }

            return this.buffersReturnedEvent.WaitOne(millisecondTimeout, false);
        }

        /// <summary>
        /// Initializes the instance for use.
        /// </summary>
        /// <param name="bufferCount">The number of buffers that will be available.</param>
        /// <param name="bufferSize">The size (in bytes) of each buffer.</param>
        /// <remarks>Two blocks of continuous unmanaged memory will be allocated.
        /// Call the <see cref="M:Dispose"/> method to free that memory.
        /// Buffer manager instances that are owned by <see cref="MidiPort"/>s will
        /// be disposed when the port is disposed.</remarks>
        public virtual void Initialize(int bufferCount, int bufferSize)
        {
            #region Method Checks

            Contract.Requires(bufferCount >= 0);
            Contract.Requires(bufferSize > 0 && bufferSize < 64 * 1024);
            Throw.IfArgumentOutOfRange(bufferCount, 0, int.MaxValue, "bufferCount");
            Throw.IfArgumentOutOfRange(bufferSize, 0, 64 * 1024, "bufferSize");
            ThrowIfDisposed();
            if (IsInitialized)
            {
                throw new InvalidOperationException("The MidiBufferManager is already initialized.");
            }

            #endregion Method Checks

            BufferCount = bufferCount;
            BufferSize = bufferSize;

            if (BufferCount > 0)
            {
                AllocateBuffers();
            }
        }

        /// <summary>
        /// Retrieves a fresh (unused) buffer for the application to use.
        /// </summary>
        /// <returns>Returns null when no more buffers are unused.</returns>
        /// <remarks>This method is only called by the application logic for an <see cref="MidiOutPort"/>
        /// or a <see cref="MidiOutStreamPort"/>. The <see cref="MidiInPort"/> registers its own buffers.</remarks>
        public virtual MidiBufferStream Retrieve()
        {
            MidiBufferStream buffer = null;

            if (this.unusedBuffers.Count > 0)
            {
                lock (this.locker)
                {
                    if (this.unusedBuffers.Count > 0)
                    {
                        buffer = this.unusedBuffers.Dequeue();
                        this.usedBuffers.Add(buffer);
                    }
                }
            }

            return buffer;
        }

        /// <summary>
        /// Returns a buffer to the manager for reuse.
        /// </summary>
        /// <param name="buffer">Must not be null.</param>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the buffer does not belong to this manager or when the buffer is not ready to be returned.
        /// </exception>
        public virtual void Return(MidiBufferStream buffer)
        {
            #region Method Checks

            Contract.Requires(buffer != null);
            Throw.IfArgumentNull(buffer, "buffer");
            ThrowIfDisposed();
            if (!this.mapBuffers.ContainsKey(buffer.HeaderMemory))
            {
                throw new InvalidOperationException("Specified buffer is not owned by this MidiBufferManager.");
            }
            if (!this.usedBuffers.Contains(buffer))
            {
                throw new InvalidOperationException("Specified buffer was not in the used buffer list of this MidiBufferManager.");
            }
            if ((buffer.HeaderFlags & NativeMethods.MHDR_INQUEUE) > 0)
            {
                throw new InvalidOperationException(Properties.Resources.MidiBufferManager_BufferStillInQueue);
            }
            // could be an error
            //if ((buffer.HeaderFlags & NativeMethods.MHDR_DONE) == 0)
            //{
            //    throw new InvalidOperationException(Properties.Resources.MidiBufferManager_BufferNotDone);
            //}

            #endregion Method Checks

            lock (this.locker)
            {
                this.usedBuffers.Remove(buffer);
                this.unusedBuffers.Enqueue(buffer);
            }
        }

        /// <summary>
        /// Called when the instance is disposed.
        /// </summary>
        /// <param name="disposing">True indicates that also (in addition to unmanaged) managed
        /// resources should be disposed.</param>
        /// <exception cref="InvalidOperationException">Thrown when not all buffers have been
        /// returned to the buffer manager.</exception>
        protected override void Dispose(bool disposing)
        {
            // I know you're not supposed to throw exceptions in Dispose.
            // But the alternative is yanking the unmanaged memory from under the buffers
            // that are still being used. That would certainly crash even the most robust
            // applications. So view this as an early warning system - as a developer head's up.
            if (this.usedBuffers.Count > 0)
            {
                throw new InvalidOperationException("Cannot call Dispose when there are still buffers in use.");
            }

            try
            {
                FreeBuffers();

                if (disposing)
                {
                    this.unusedBuffers.Clear();
                    this.usedBuffers.Clear();
                    this.mapBuffers.Clear();

                    this.buffersReturnedEvent.Close();
                }
            }
            finally
            {
                base.Dispose(disposing);
            }
        }

        /// <summary>
        /// Called when a buffer needs to be prepared for use.
        /// </summary>
        /// <param name="buffer">Must not be null.</param>
        protected abstract void OnPrepareBuffer(MidiBufferStream buffer);

        /// <summary>
        /// Called when a buffer needs to be un-prepared after use.
        /// </summary>
        /// <param name="buffer">Must not be null.</param>
        protected abstract void OnUnprepareBuffer(MidiBufferStream buffer);

        /// <summary>
        /// Finds a buffer instance based on the <see cref="MidiHeader"/> that is used by the <see cref="MidiPort"/> implementations.
        /// </summary>
        /// <param name="headerMemory">A pointer to the midi header structure.</param>
        /// <returns>Returns null if the buffer was not found.</returns>
        internal MidiBufferStream FindBuffer(IntPtr headerMemory)
        {
            ThrowIfDisposed();

            return this.mapBuffers[headerMemory];
        }

        /// <summary>
        /// Allocates the unmanaged memory for the midi headers and buffers.
        /// </summary>
        private void AllocateBuffers()
        {
            memHeaders = MemoryUtil.Alloc(MemoryUtil.SizeOfMidiHeader * BufferCount);
            memBuffers = MemoryUtil.Alloc(BufferSize * BufferCount);
            GC.AddMemoryPressure((MemoryUtil.SizeOfMidiHeader + BufferSize) * BufferCount);

            IntPtr pHeader = IntPtr.Add(memHeaders, 0);
            IntPtr pBuffer = IntPtr.Add(memBuffers, 0);

            for (int i = 0; i < BufferCount; i++)
            {
                var buffer = new MidiBufferStream(pHeader, pBuffer, BufferSize, StreamAccess);
                this.unusedBuffers.Enqueue(buffer);
                this.mapBuffers.Add(pHeader, buffer);

                pHeader = IntPtr.Add(pHeader, MemoryUtil.SizeOfMidiHeader);
                pBuffer = IntPtr.Add(pBuffer, BufferSize);
            }
        }

        /// <summary>
        /// Frees the allocated memory.
        /// </summary>
        private void FreeBuffers()
        {
            if (this.memHeaders != IntPtr.Zero)
            {
                MemoryUtil.Free(this.memHeaders);
                this.memHeaders = IntPtr.Zero;
            }

            if (this.memBuffers != IntPtr.Zero)
            {
                MemoryUtil.Free(this.memBuffers);
                this.memBuffers = IntPtr.Zero;
            }

            var totalLength = (MemoryUtil.SizeOfMidiHeader + BufferSize) * BufferCount;

            if (totalLength > 0)
            {
                GC.RemoveMemoryPressure(totalLength);
            }
        }

        /// <summary>
        /// Loops through all the buffers and calls the <see cref="M:OnPrepareBuffer"/> for each one.
        /// </summary>
        protected internal virtual void PrepareAllBuffers()
        {
            ThrowIfDisposed();

            foreach (var buffer in this.mapBuffers.Values)
            {
                OnPrepareBuffer(buffer);
            }
        }

        /// <summary>
        /// Loops through all the buffers and calls the <see cref="M:OnUnprepareBuffer"/> for each one.
        /// </summary>
        protected internal virtual void UnprepareAllBuffers()
        {
            ThrowIfDisposed();

            foreach (var buffer in this.mapBuffers.Values)
            {
                OnUnprepareBuffer(buffer);
            }
        }
    }

    /// <summary>
    /// Abstract class template for contracts for abstract <see cref="MidiBufferManager"/> class.
    /// </summary>
    internal abstract class MidiBufferManagerContract : MidiBufferManager
    {
        private MidiBufferManagerContract()
            : base(null, FileAccess.Read)
        { }

        protected override void OnPrepareBuffer(MidiBufferStream buffer)
        {
            Contract.Requires(buffer != null);

            throw new NotImplementedException();
        }

        protected override void OnUnprepareBuffer(MidiBufferStream buffer)
        {
            Contract.Requires(buffer != null);

            throw new NotImplementedException();
        }
    }
}