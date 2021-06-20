using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Sparrow;
using Sparrow.Binary;
using Sparrow.Logging;
using Sparrow.LowMemory;
using Sparrow.Platform;
using Sparrow.Server;
using Sparrow.Server.Platform;
using Sparrow.Threading;
using Sparrow.Utils;
using Voron.Data;
using Voron.Exceptions;
using Voron.Platform;
using Voron.Util.Settings;
using Constants = Voron.Global.Constants;

namespace Voron.Impl.Paging
{
    public abstract unsafe class AbstractPager : IDisposable, ILowMemoryHandler
    {
        public readonly Logger Log = LoggingSource.Instance.GetLogger<AbstractPager>("AbstractPager");
        private readonly StorageEnvironmentOptions _options;

        public static ConcurrentDictionary<string, uint> PhysicalDrivePerMountCache = new ConcurrentDictionary<string, uint>();

        protected static readonly object WorkingSetIncreaseLocker = new object();

        protected int MinIncreaseSize => 16 * Constants.Size.Kilobyte;

        protected int MaxIncreaseSize => Constants.Size.Gigabyte;

        private long _increaseSize;
        private DateTime _lastIncrease;
        private readonly object _pagerStateModificationLocker = new object();
        public readonly bool UsePageProtection;
        private readonly MultipleUseFlag _lowMemoryFlag = new MultipleUseFlag();
        protected readonly bool CanPrefetchAhead;

        public Action<PagerState> PagerStateChanged;

        public Func<long> AllocatedInBytesFunc;

        public long GetAllocatedInBytes()
        {
            return AllocatedInBytesFunc?.Invoke() ?? NumberOfAllocatedPages * Constants.Storage.PageSize;
        }

        protected AbstractPager()
        {
            CanPrefetch = new Lazy<bool>(CanPrefetchQuery);
        }
        
        public void SetPagerState(PagerState newState)
        {
            if (DisposeOnceRunner.Disposed)
                ThrowAlreadyDisposedException();

            lock (_pagerStateModificationLocker)
            {
                newState.AddRef();

                if (ShouldLockMemoryAtPagerLevel())
                {
                    // Note: This is handled differently in 32-bits.
                    // Locking/unlocking the memory is done separately for each mapping.
                    try
                    {
                        foreach (var info in newState.AllocationInfos)
                        {
                            if (info.Size == 0 || info.BaseAddress == null)
                                continue;

                            if (Sodium.sodium_mlock(info.BaseAddress, (UIntPtr)info.Size) != 0)
                            {
                                if (DoNotConsiderMemoryLockFailureAsCatastrophicError)
                                    continue; // okay, can skip this, then

                                lock (WorkingSetIncreaseLocker)
                                {
                                    // we should check ourselves again after we enter the lock, someone could halve already increased working set enough
                                    if (Sodium.sodium_mlock(info.BaseAddress, (UIntPtr)info.Size) != 0)
                                        TryHandleFailureToLockMemory(info.BaseAddress, info.Size);
                                 
                                }
                            }
                        }
                    }
                    catch
                    {
                        // need to restore the state to the way it was, so we'll dispose the pager state
                        newState.Release();
                        throw;
                    }
                }

                _debugInfo = GetSourceName();
                var oldState = _pagerState;
                _pagerState = newState;
                PagerStateChanged?.Invoke(newState);
                oldState?.Release();
            }
        }

        public virtual bool ShouldLockMemoryAtPagerLevel()
        {
            return LockMemory;
        }

        protected void TryHandleFailureToLockMemory(byte* addressToLock, long sizeToLock)
        {
            var currentProcess = Process.GetCurrentProcess();

            if (PlatformDetails.RunningOnPosix == false)
            {
                // From: https://msdn.microsoft.com/en-us/library/windows/desktop/ms686234(v=vs.85).aspx
                // "The maximum number of pages that a process can lock is equal to the number of pages in its minimum working set minus a small overhead"
                // let's increase the max size of memory we can lock by increasing the MinWorkingSet. On Windows, that is available for all users
                var nextWorkingSetSize = GetNearestFileSize(currentProcess.MinWorkingSet.ToInt64() + sizeToLock);

                if (nextWorkingSetSize > int.MaxValue && PlatformDetails.Is32Bits)
                {
                    nextWorkingSetSize = int.MaxValue;
                }

                // Minimum working set size must be less than or equal to the maximum working set size.
                // Let's increase the max as well.
                if (nextWorkingSetSize > (long)currentProcess.MaxWorkingSet)
                {
                    try
                    {
                        currentProcess.MaxWorkingSet = new IntPtr(nextWorkingSetSize);
                    }
                    catch (Exception e)
                    {
                        throw new InsufficientMemoryException($"Need to increase the min working set size from {(long)currentProcess.MinWorkingSet:#,#;;0} bytes to {nextWorkingSetSize:#,#;;0} bytes but the max working set size was too small: {(long)currentProcess.MaxWorkingSet:#,#;;0}. " +
                                                              $"Failed to increase the max working set size so we can lock {sizeToLock:#,#;;0} bytes for {FileName}. With encrypted " +
                                                              "databases we lock some memory in order to avoid leaking secrets to disk. Treating this as a catastrophic error " +
                                                              "and aborting the current operation.", e);
                    }
                }

                try
                {
                    currentProcess.MinWorkingSet = new IntPtr(nextWorkingSetSize);
                }
                catch (Exception e)
                {
                    throw new InsufficientMemoryException($"Failed to increase the min working set size to {nextWorkingSetSize:#,#;;0} bytes so we can lock {sizeToLock:#,#;;0} bytes for {FileName}. With encrypted " +
                                                          "databases we lock some memory in order to avoid leaking secrets to disk. Treating this as a catastrophic error " +
                                                          "and aborting the current operation.", e);
                }

                // now we can try again, after we raised the limit, we only do so once, though
                if (Sodium.sodium_mlock(addressToLock, (UIntPtr)sizeToLock) == 0)
                    return;
            }

            var msg =
                $"Unable to lock memory for {FileName} with size {sizeToLock:#,#;;0} bytes), with encrypted databases we lock some memory in order to avoid leaking secrets to disk. Treating this as a catastrophic error and aborting the current operation.{Environment.NewLine}";
            if (PlatformDetails.RunningOnPosix)
            {
                msg +=
                    $"The admin may configure higher limits using: 'sudo prlimit --pid {currentProcess.Id} --memlock={sizeToLock}' to increase the limit. (It's recommended to do that as part of the startup script){Environment.NewLine}";
            }
            else
            {
                msg +=
                    $"Already tried to raise the the process min working set to {currentProcess.MinWorkingSet.ToInt64():#,#;;0} bytes but still got a failure.{Environment.NewLine}";
            }

            msg += "This behavior is controlled by the 'Security.DoNotConsiderMemoryLockFailureAsCatastrophicError' setting (expert only, modifications of this setting is not recommended).";

            throw new InsufficientMemoryException(msg);
        }

        internal PagerState GetPagerStateAndAddRefAtomically()
        {
            if (DisposeOnceRunner.Disposed)
                ThrowAlreadyDisposedException();

            lock (_pagerStateModificationLocker)
            {
                if (_pagerState == null)
                    return null;
                _pagerState.AddRef();
                return _pagerState;
            }
        }

        public PagerState PagerState
        {
            get
            {
                if (DisposeOnceRunner.Disposed)
                    ThrowAlreadyDisposedException();
                return _pagerState;
            }
        }

        private string _debugInfo;

        public string DebugInfo => _debugInfo;

        public const int PageMaxSpace = Constants.Storage.PageSize - Constants.Tree.PageHeaderSize;

        public VoronPathSetting FileName { get; protected set; }

        protected AbstractPager(StorageEnvironmentOptions options, bool canPrefetchAhead, bool usePageProtection = false) : this()
        {
            DisposeOnceRunner = new DisposeOnce<SingleAttempt>(() =>
            {
                if (FileName?.FullPath != null)
                    _options?.IoMetrics?.FileClosed(FileName.FullPath);

                if (_pagerState != null)
                {
                    _pagerState.Release();
                    _pagerState = null;
                }

                if (FileName?.FullPath != null)
                    NativeMemory.UnregisterFileMapping(FileName.FullPath);

                DisposeInternal();
            });

            _options = options;
            CanPrefetchAhead = canPrefetchAhead && _options.EnablePrefetching;
            UsePageProtection = usePageProtection;
            Debug.Assert((Constants.Storage.PageSize - Constants.Tree.PageHeaderSize) / Constants.Tree.MinKeysInPage >= 1024);

            NodeMaxSize = PageMaxSpace / 2 - 1;

            // MaxNodeSize is usually persisted as an unsigned short. Therefore, we must ensure it is not possible to have an overflow.
            Debug.Assert(NodeMaxSize < ushort.MaxValue);

            _increaseSize = MinIncreaseSize;
            PageMinSpace = (int)(PageMaxSpace * 0.33);

            SetPagerState(new PagerState(this, options.PrefetchSegmentSize, options.PrefetchResetThreshold));

            LowMemoryNotification.Instance.RegisterLowMemoryHandler(this);
        }

        public StorageEnvironmentOptions Options => _options;

        public int PageMinSpace
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private set;
        }

        public bool DeleteOnClose
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set;
        }

        public int NodeMaxSize
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private set;
        }

        public const int RequiredSpaceForNewNode = Constants.Tree.NodeHeaderSize + Constants.Tree.NodeOffsetSize;

        protected PagerState _pagerState;

        public virtual long NumberOfAllocatedPages { get; protected set; }
        public abstract long TotalAllocationSize { get; }

        protected abstract string GetSourceName();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte* AcquirePagePointerInternal(IPagerLevelTransactionState tx, long pageNumber, PagerState pagerState)
        {
            var state = pagerState;

            if (DisposeOnceRunner.Disposed)
                goto AlreadyDisposed;

            if (pageNumber > NumberOfAllocatedPages || pageNumber < 0)
                goto InvalidPageNumber;

            if (state == null)
            {
                state = _pagerState;

                if (state == null)
                    goto AlreadyDisposed;
            }

            tx?.EnsurePagerStateReference(ref state);
            
            if (state._released)
                goto InvalidPagerState;

            return state.MapBase + pageNumber * Constants.Storage.PageSize;

        AlreadyDisposed:
            ThrowAlreadyDisposedException();
        InvalidPageNumber:
            ThrowOnInvalidPageNumber(pageNumber);
        InvalidPagerState:
            // ReSharper disable once PossibleNullReferenceException
            state.ThrowInvalidPagerState();

            return null; // Will never happen. 
        }

        public virtual byte* AcquirePagePointer(IPagerLevelTransactionState tx, long pageNumber, PagerState pagerState = null)
        {
            return AcquirePagePointerInternal(tx, pageNumber, pagerState);
        }

        public virtual byte* AcquirePagePointerForNewPage(IPagerLevelTransactionState tx, long pageNumber, int numberOfPages, PagerState pagerState = null)
        {
            return AcquirePagePointerInternal(tx, pageNumber, pagerState);
        }

        public virtual void BreakLargeAllocationToSeparatePages(IPagerLevelTransactionState tx, long pageNumber)
        {
            // This method is implemented only in Crypto Pager
        }

        public abstract void Sync(long totalUnsynced);

        public PagerState EnsureContinuous(long requestedPageNumber, int numberOfPages)
        {
            if (DisposeOnceRunner.Disposed)
                ThrowAlreadyDisposedException();

            if (requestedPageNumber + numberOfPages <= NumberOfAllocatedPages)
                return null;

            // this ensure that if we want to get a range that is more than the current expansion
            // we will increase as much as needed in one shot
            var minRequested = (requestedPageNumber + numberOfPages) * Constants.Storage.PageSize;
            var allocationSize = Math.Max(NumberOfAllocatedPages * Constants.Storage.PageSize, Constants.Storage.PageSize);
            while (minRequested > allocationSize)
            {
                allocationSize = GetNewLength(allocationSize, minRequested);
            }

            if (_options.CopyOnWriteMode && FileName.FullPath.EndsWith(Constants.DatabaseFilename))
                ThrowIncreasingDataFileInCopyOnWriteModeException(FileName.FullPath, allocationSize);

            return AllocateMorePages(allocationSize);
        }

        [Conditional("VALIDATE")]
        internal virtual void ProtectPageRange(byte* start, ulong size, bool force = false)
        {
            // This method is currently implemented only in WindowsMemoryMapPager and POSIX
        }

        [Conditional("VALIDATE")]
        internal virtual void UnprotectPageRange(byte* start, ulong size, bool force = false)
        {
            // This method is currently implemented only in WindowsMemoryMapPager and POSIX
        }

        public uint UniquePhysicalDriveId;
        protected readonly DisposeOnce<SingleAttempt> DisposeOnceRunner;
        public bool Disposed => DisposeOnceRunner.Disposed;

        /// <summary>
        /// This determine whatever we'll attempt to lock the memory
        /// so it will not go to the swap / core dumps
        /// </summary>
        public bool LockMemory;

        /// <summary>
        /// Control whatever we should treat memory lock errors as catastrophic errors
        /// or not. By default, we consider them catastrophic and fail immediately to
        /// avoid leaking any data. 
        /// </summary>
        public bool DoNotConsiderMemoryLockFailureAsCatastrophicError;

        protected abstract void DisposeInternal();

        public void Dispose()
        {
            DisposeOnceRunner.Dispose();
            GC.SuppressFinalize(this);
        }

        ~AbstractPager()
        {
            // Should not reach here (GC.SuppressFinalize was called from must to be called 'Dispose')
            try
            {
                DisposeOnceRunner.Dispose();
            }
            catch (Exception e)
            {
                try
                {
                    if (Log.IsInfoEnabled)
                        Log.Info("AbstractPager finalizer was called (leak?), and 'DisposeOnceRunner.Dispose' threw exception", e);
                }
                catch
                {
                    // ignore
                }
            }
            finally
            {
                try
                {
                    if (Log.IsInfoEnabled)
                        Log.Info("AbstractPager finalizer was called although GC.SuppressFinalize should have been called", new InvalidOperationException("Leak in "));
                }
                catch
                {
                    // ignore
                }
            }
        }

        protected internal abstract PagerState AllocateMorePages(long newLength);

        private long GetNewLength(long current, long minRequested)
        {
            DateTime now = DateTime.UtcNow;
            if (_lastIncrease == DateTime.MinValue)
            {
                _lastIncrease = now;
                return MinIncreaseSize;
            }

            if (_lowMemoryFlag)
            {
                _lastIncrease = now;
                // cannot return less than the minRequested
                return GetNearestFileSize(minRequested);
            }

            TimeSpan timeSinceLastIncrease = (now - _lastIncrease);
            _lastIncrease = now;
            if (timeSinceLastIncrease.TotalMinutes < 3)
            {
                _increaseSize = Math.Min(_increaseSize * 2, MaxIncreaseSize);
            }
            else if (timeSinceLastIncrease.TotalMinutes > 15)
            {
                _increaseSize = Math.Max(MinIncreaseSize, _increaseSize / 2);
            }

            // At any rate, we won't do an increase by over 50% of current size, to prevent huge empty spaces
            // 
            // The reasoning behind this is that we want to make sure that we increase in size very slowly at first
            // because users tend to be sensitive to a lot of "wasted" space. 
            // We also consider the fact that small increases in small files would probably result in cheaper costs, and as
            // the file size increases, we will reserve more & more from the OS.
            // This also avoids "I added 300 records and the file size is 64MB" problems that occur when we are too
            // eager to reserve space
            var actualIncrease = Math.Min(_increaseSize, current / 2);

            // we then want to get the next power of two number, to get pretty file size
            var totalSize = current + actualIncrease;
            return GetNearestFileSize(totalSize);
        }

        private static readonly long IncreaseByPowerOf2Threshold = new Size(512, SizeUnit.Megabytes).GetValue(SizeUnit.Bytes);

        private static long GetNearestFileSize(long neededSize)
        {
            if (neededSize < IncreaseByPowerOf2Threshold)
                return Bits.PowerOf2(neededSize);

            // if it is over 0.5 GB, then we grow at 1 GB intervals
            var remainder = neededSize % Constants.Size.Gigabyte;
            if (remainder == 0)
                return neededSize;

            // above 0.5GB we need to round to the next GB number
            return neededSize + Constants.Size.Gigabyte - remainder;
        }

        public abstract override string ToString();


        public void ThrowAlreadyDisposedException()
        {
            // this is a separate method because we don't want to have an exception throwing in the hot path
            throw new ObjectDisposedException($"The pager is already disposed ({ToString()})");
        }

        protected void ThrowOnInvalidPageNumber(long pageNumber)
        {
            // this is a separate method because we don't want to have an exception throwing in the hot path

            VoronUnrecoverableErrorException.Raise(_options,
                "The page " + pageNumber + " was not allocated, allocated pages: " + NumberOfAllocatedPages + " in " +
                GetSourceName());
        }

        public static void ThrowIncreasingDataFileInCopyOnWriteModeException(string dataFilePath, long requestedSize)
        {
            throw new IncreasingDataFileInCopyOnWriteModeException(dataFilePath, requestedSize);
        }

        public virtual void ReleaseAllocationInfo(byte* baseAddress, long size)
        {
            if (LockMemory == false)
                return;

            // intentionally skipping verification of the result, nothing that we
            // can do about this here, and we are going to free the memory anyway
            // that at any rate, we don't care about the memory zeroing, since
            // we are already zeroing the memory ourselves (the pager is likely
            // to be part of a long term held instance).
            Sodium.sodium_munlock(baseAddress, (UIntPtr)size);
        }

        // NodeMaxSize - RequiredSpaceForNewNode for 4Kb page is 2038, so we drop this by a bit
        public const int MaxKeySize = 2038 - RequiredSpaceForNewNode;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsKeySizeValid(int keySize)
        {
            if (keySize > MaxKeySize)
                return false;

            return true;
        }

        public readonly Lazy<bool> CanPrefetch;

        protected virtual bool CanPrefetchQuery()
        {
            if (PlatformDetails.CanPrefetch == false || CanPrefetchAhead == false)
                return false; // not supported

            return true;
        }

        private struct PageIterator : IEnumerator<long>
        {
            private readonly long _startPage;
            private readonly long _endPage;
            private long _currentPage;

            public PageIterator(long pageNumber, int pagesToPrefetch)
            {
                this._startPage = pageNumber;
                this._endPage = pageNumber + pagesToPrefetch;
                this._currentPage = pageNumber;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool MoveNext()
            {
                this._currentPage++;
                return _currentPage < _endPage;
            }

            public void Reset()
            {
                this._currentPage = this._startPage;
            }

            public long Current
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => _currentPage;
            }

            object IEnumerator.Current => Current;

            public void Dispose() {}
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void MaybePrefetchMemory(long pageNumber, int pagesToPrefetch)
        {
            MaybePrefetchMemory(new PageIterator(pageNumber, pagesToPrefetch));
        }

        public void MaybePrefetchMemory<T>(T pagesToPrefetch) where T : struct, IEnumerator<long>
        {
            if (!this.CanPrefetch.Value)
                return;

            if (pagesToPrefetch.MoveNext() == false)
                return;

            var prefetcher = GlobalPrefetchingBehavior.GlobalPrefetcher.Value;

            PalDefinitions.PrefetchRanges command = default;
            do
            {
                long pageNumber = pagesToPrefetch.Current;
                if (this._pagerState.ShouldPrefetchSegment(pageNumber, out void* virtualAddress, out long bytes))
                {
                    command.VirtualAddress = virtualAddress;
                    command.NumberOfBytes = new IntPtr(bytes);
                    prefetcher.CommandQueue.TryAdd(command, 0);
                }
            }
            while (pagesToPrefetch.MoveNext());

            this._pagerState.CheckResetPrefetchTable();
        }

        public virtual void DiscardPages(long pageNumber, int numberOfPages)
        {
            var pagerState = PagerState;
            Debug.Assert(pagerState.AllocationInfos.Length == 1);

            var allocInfo = pagerState.AllocationInfos[0];

            byte* baseAddress = allocInfo.BaseAddress;
            long offset = pageNumber * Constants.Storage.PageSize;

            Pal.rvn_discard_virtual_memory(baseAddress + offset, numberOfPages * Constants.Storage.PageSize, out _);
        }

        public virtual void DiscardWholeFile()
        {
            long size = 0;
            void* baseAddress = null;
            var pagerState = PagerState;
            Debug.Assert(pagerState.AllocationInfos.Length == 1);

            for (int i = 0; i < pagerState.AllocationInfos.Length; i++)
            {
                var allocInfo = pagerState.AllocationInfos[i];
                size += allocInfo.Size;

                if (baseAddress == null)
                    baseAddress = allocInfo.BaseAddress;

                if (i != pagerState.AllocationInfos.Length - 1 &&
                    pagerState.AllocationInfos[i + 1].BaseAddress == allocInfo.BaseAddress + allocInfo.Size)
                {
                    continue; // if adjacent ranges make one syscall
                }

                Pal.rvn_discard_virtual_memory(baseAddress, size, out _);

                size = 0;
                baseAddress = null;
            }
        }

        public void TryPrefetchingWholeFile()
        {
            if (PlatformDetails.CanPrefetch == false || CanPrefetchAhead == false)
                return; // not supported

            var pagerState = PagerState;
            Debug.Assert(pagerState.AllocationInfos.Length == 1);

            var prefetcher = GlobalPrefetchingBehavior.GlobalPrefetcher.Value;
            var command = default(PalDefinitions.PrefetchRanges);

            long size = 0;
            byte* baseAddress = null;

            // we will change the array into a single value in next version
            for (int i = 0; i < pagerState.AllocationInfos.Length; i++)
            {
                var allocInfo = pagerState.AllocationInfos[i];
                size += allocInfo.Size;

                if (baseAddress == null)
                    baseAddress = allocInfo.BaseAddress;

                if (i != pagerState.AllocationInfos.Length - 1 &&
                    pagerState.AllocationInfos[i + 1].BaseAddress == allocInfo.BaseAddress + allocInfo.Size)
                {
                    continue; // if adjacent ranges make one syscall
                }

                command.VirtualAddress = baseAddress;
                command.NumberOfBytes = new IntPtr(size);
                prefetcher.CommandQueue.TryAdd(command, 0);

                size = 0;
                baseAddress = null;
            }
        }


        public virtual bool EnsureMapped(IPagerLevelTransactionState tx, long page, int numberOfPages)
        {
            // nothing to do
            return false;
        }

        public abstract int CopyPage(I4KbBatchWrites destwI4KbBatchWrites, long p, PagerState pagerState);

        protected int CopyPageImpl(I4KbBatchWrites destwI4KbBatchWrites, long p, PagerState pagerState)
        {
            var src = AcquirePagePointer(null, p, pagerState);
            var pageHeader = (PageHeader*)src;
            int numberOfPages = 1;
            if ((pageHeader->Flags & PageFlags.Overflow) == PageFlags.Overflow)
            {
                numberOfPages = VirtualPagerLegacyExtensions.GetNumberOfOverflowPages(pageHeader->OverflowSize);
            }
            const int adjustPageSize = (Constants.Storage.PageSize) / (4 * Constants.Size.Kilobyte);
            destwI4KbBatchWrites.Write(pageHeader->PageNumber * (long)adjustPageSize, numberOfPages * adjustPageSize, src);

            return numberOfPages;
        }

        public virtual I4KbBatchWrites BatchWriter()
        {
            return new Simple4KbBatchWrites(this);
        }

        public virtual byte* AcquireRawPagePointer(IPagerLevelTransactionState tx, long pageNumber, PagerState pagerState = null)
        {
            return AcquirePagePointer(tx, pageNumber, pagerState);
        }

        public void LowMemory(LowMemorySeverity lowMemorySeverity)
        {
            // We could check for nested calls to LowMemory here, but we 
            // probably don't want to error because of it.
            _lowMemoryFlag.Raise();
        }

        public void LowMemoryOver()
        {
            _lowMemoryFlag.Lower();
        }
    }

    public interface I4KbBatchWrites : IDisposable
    {
        unsafe void Write(long posBy4Kbs, int numberOf4Kbs, byte* source);
    }

    public unsafe class Simple4KbBatchWrites : I4KbBatchWrites
    {
        private readonly AbstractPager _abstractPager;
        private PagerState _pagerState;

        public Simple4KbBatchWrites(AbstractPager abstractPager)
        {
            _abstractPager = abstractPager;
            _pagerState = _abstractPager.GetPagerStateAndAddRefAtomically();
        }

        public void Write(long posBy4Kbs, int numberOf4Kbs, byte* source)
        {
            const int pageSizeTo4KbRatio = (Constants.Storage.PageSize / (4 * Constants.Size.Kilobyte));
            var pageNumber = posBy4Kbs / pageSizeTo4KbRatio;
            var offsetBy4Kb = posBy4Kbs % pageSizeTo4KbRatio;
            var numberOfPages = numberOf4Kbs / pageSizeTo4KbRatio;
            if (numberOf4Kbs % pageSizeTo4KbRatio != 0)
                numberOfPages++;

            var newPagerState = _abstractPager.EnsureContinuous(pageNumber, numberOfPages);
            if (newPagerState != null)
            {
                _pagerState.Release();
                newPagerState.AddRef();
                _pagerState = newPagerState;
            }

            var toWrite = numberOf4Kbs * 4 * Constants.Size.Kilobyte;
            byte* destination = _abstractPager.AcquirePagePointer(null, pageNumber, _pagerState)
                                + (offsetBy4Kb * 4 * Constants.Size.Kilobyte);

            _abstractPager.UnprotectPageRange(destination, (ulong)toWrite);

            Memory.Copy(destination, source, toWrite);

            _abstractPager.ProtectPageRange(destination, (ulong)toWrite);
        }


        public void Dispose()
        {
            _pagerState.Release();
        }
    }
}

