﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Raven.Abstractions;
using Raven.Abstractions.Data;
using Raven.Abstractions.Extensions;
using Raven.Abstractions.Indexing;
using Raven.Abstractions.Logging;
using Raven.Client.Data;
using Raven.Client.Data.Indexes;
using Raven.Client.Indexing;
using Raven.Server.Documents.Indexes.Auto;
using Raven.Server.Documents.Indexes.Persistence.Lucene;
using Raven.Server.Documents.Indexes.Workers;
using Raven.Server.Documents.Queries;
using Raven.Server.Documents.Queries.Results;
using Raven.Server.Exceptions;
using Raven.Server.ServerWide;
using Raven.Server.ServerWide.Context;
using Raven.Server.Utils;

using Sparrow;
using Sparrow.Json;
using Voron;

namespace Raven.Server.Documents.Indexes
{
    public abstract class Index<TIndexDefinition> : Index
        where TIndexDefinition : IndexDefinitionBase
    {
        public new TIndexDefinition Definition => (TIndexDefinition)base.Definition;

        protected Index(int indexId, IndexType type, TIndexDefinition definition)
            : base(indexId, type, definition)
        {
        }
    }

    public abstract class Index : IDocumentTombstoneAware, IDisposable
    {
        private long writeErrors;

        private const long WriteErrorsLimit = 10;

        protected readonly ILog Log = LogManager.GetLogger(typeof(Index));

        protected readonly LuceneIndexPersistence IndexPersistence;

        private readonly object _locker = new object();

        private readonly AsyncManualResetEvent _indexingBatchCompleted = new AsyncManualResetEvent();

        private CancellationTokenSource _cancellationTokenSource;

        protected DocumentDatabase DocumentDatabase;

        private Thread _indexingThread;

        private bool _initialized;

        private UnmanagedBuffersPool _unmanagedBuffersPool;

        private StorageEnvironment _environment;

        protected TransactionContextPool _contextPool;

        private bool _disposed;

        protected readonly ManualResetEventSlim _mre = new ManualResetEventSlim();

        private DateTime? _lastQueryingTime;

        public readonly HashSet<string> Collections;

        private volatile bool _indexingInProgress;

        internal IndexStorage _indexStorage;
        private IIndexingWork[] _indexWorkers;

        protected Index(int indexId, IndexType type, IndexDefinitionBase definition)
        {
            if (indexId <= 0)
                throw new ArgumentException("IndexId must be greater than zero.", nameof(indexId));

            IndexId = indexId;
            Type = type;
            Definition = definition;
            IndexPersistence = new LuceneIndexPersistence(indexId, definition, type);
            Collections = new HashSet<string>(Definition.Collections, StringComparer.OrdinalIgnoreCase);
        }

        public static Index Open(int indexId, DocumentDatabase documentDatabase)
        {
            StorageEnvironment environment = null;

            var options = StorageEnvironmentOptions.ForPath(Path.Combine(documentDatabase.Configuration.Indexing.IndexStoragePath, indexId.ToString()));
            try
            {
                options.SchemaVersion = 1;

                environment = new StorageEnvironment(options);
                var type = IndexStorage.ReadIndexType(indexId, environment);

                switch (type)
                {
                    case IndexType.AutoMap:
                        return AutoMapIndex.Open(indexId, environment, documentDatabase);
                    default:
                        throw new NotImplementedException();
                }
            }
            catch (Exception)
            {
                if (environment != null)
                    environment.Dispose();
                else
                    options.Dispose();

                throw;
            }
        }

        public int IndexId { get; }

        public IndexType Type { get; }

        public IndexingPriority Priority { get; private set; }

        public IndexDefinitionBase Definition { get; }

        public string Name => Definition?.Name;

        public bool IsRunning => _indexingThread != null;

        protected void Initialize(DocumentDatabase documentDatabase)
        {
            lock (_locker)
            {
                if (_initialized)
                    throw new InvalidOperationException($"Index '{Name} ({IndexId})' was already initialized.");

                var options = documentDatabase.Configuration.Indexing.RunInMemory
                    ? StorageEnvironmentOptions.CreateMemoryOnly()
                    : StorageEnvironmentOptions.ForPath(Path.Combine(documentDatabase.Configuration.Indexing.IndexStoragePath, IndexId.ToString()));

                options.SchemaVersion = 1;
                try
                {
                    Initialize(new StorageEnvironment(options), documentDatabase);
                }
                catch (Exception)
                {
                    options.Dispose();
                    throw;
                }
            }
        }

        protected void Initialize(StorageEnvironment environment, DocumentDatabase documentDatabase)
        {
            if (_disposed)
                throw new ObjectDisposedException($"Index '{Name} ({IndexId})' was already disposed.");

            lock (_locker)
            {
                if (_initialized)
                    throw new InvalidOperationException($"Index '{Name} ({IndexId})' was already initialized.");

                try
                {
                    Debug.Assert(Definition != null);

                    DocumentDatabase = documentDatabase;
                    _environment = environment;
                    _unmanagedBuffersPool = new UnmanagedBuffersPool($"Indexes//{IndexId}");
                    _contextPool = new TransactionContextPool(_unmanagedBuffersPool, _environment);
                    _indexStorage = new IndexStorage(this, _contextPool);

                    _indexStorage.Initialize(_environment);
                    IndexPersistence.Initialize(_environment, DocumentDatabase.Configuration.Indexing);

                    LoadValues();

                    DocumentDatabase.DocumentTombstoneCleaner.Subscribe(this);

                    DocumentDatabase.Notifications.OnIndexChange += HandleIndexChange;

                    _indexWorkers = CreateIndexWorkExecutors();

                    _initialized = true;
                }
                catch (Exception)
                {
                    Dispose();
                    throw;
                }
            }
        }

        protected virtual void LoadValues()
        {
            TransactionOperationContext context;
            using (_contextPool.AllocateOperationContext(out context))
            using (var tx = context.OpenReadTransaction())
            {
                Priority = _indexStorage.ReadPriority(tx);
                _lastQueryingTime = SystemTime.UtcNow;
            }
        }

        public void Start()
        {
            if (_disposed)
                throw new ObjectDisposedException($"Index '{Name} ({IndexId})' was already disposed.");

            if (_initialized == false)
                throw new InvalidOperationException($"Index '{Name} ({IndexId})' was not initialized.");

            lock (_locker)
            {
                if (_indexingThread != null)
                    throw new InvalidOperationException($"Index '{Name} ({IndexId})' is executing.");

                if (DocumentDatabase.Configuration.Indexing.Disabled)
                    return;

                _cancellationTokenSource = new CancellationTokenSource();

                _indexingThread = new Thread(ExecuteIndexing)
                {
                    Name = "Indexing of " + Name,
                    IsBackground = true
                };

                _indexingThread.Start();
            }
        }

        public void Stop()
        {
            if (_disposed)
                throw new ObjectDisposedException($"Index '{Name} ({IndexId})' was already disposed.");

            if (_initialized == false)
                throw new InvalidOperationException($"Index '{Name} ({IndexId})' was not initialized.");

            lock (_locker)
            {
                if (_indexingThread == null)
                    return;

                _cancellationTokenSource.Cancel();

                var indexingThread = _indexingThread;
                _indexingThread = null;
                indexingThread.Join();
            }
        }

        public void Dispose()
        {
            lock (_locker)
            {
                if (_disposed)
                    return;

                _disposed = true;

                _cancellationTokenSource?.Cancel();

                DocumentDatabase.DocumentTombstoneCleaner.Unsubscribe(this);

                DocumentDatabase.Notifications.OnIndexChange -= HandleIndexChange;

                var exceptionAggregator = new ExceptionAggregator(Log, $"Could not dispose {nameof(Index)} '{Name}'");

                exceptionAggregator.Execute(() =>
                {
                    _indexingThread?.Join();
                    _indexingThread = null;
                });

                exceptionAggregator.Execute(() =>
                {
                    _environment?.Dispose();
                    _environment = null;
                });

                exceptionAggregator.Execute(() =>
                {
                    _unmanagedBuffersPool?.Dispose();
                    _unmanagedBuffersPool = null;
                });

                exceptionAggregator.Execute(() =>
                {
                    _contextPool?.Dispose();
                    _contextPool = null;
                });

                exceptionAggregator.ThrowIfNeeded();
            }
        }

        public bool IsStale(DocumentsOperationContext databaseContext)
        {
            Debug.Assert(databaseContext.Transaction != null);

            TransactionOperationContext indexContext;
            using (_contextPool.AllocateOperationContext(out indexContext))
            using (indexContext.OpenReadTransaction())
            {
                return IsStale(databaseContext, indexContext);
            }
        }

        protected virtual bool IsStale(DocumentsOperationContext databaseContext, TransactionOperationContext indexContext, long? cutoff = null)
        {
            foreach (var collection in Collections)
            {
                var lastDocEtag = DocumentDatabase.DocumentsStorage.GetLastDocumentEtag(databaseContext, collection);
                var lastProcessedDocEtag = _indexStorage.ReadLastMappedEtag(indexContext.Transaction, collection);

                if (cutoff == null)
                {
                    if (lastDocEtag > lastProcessedDocEtag)
                        return true;

                    var lastTombstoneEtag = DocumentDatabase.DocumentsStorage.GetLastTombstoneEtag(databaseContext, collection);
                    var lastProcessedTombstoneEtag = _indexStorage.ReadLastProcessedTombstoneEtag(indexContext.Transaction, collection);

                    if (lastTombstoneEtag > lastProcessedTombstoneEtag)
                        return true;
                }
                else
                {
                    if (Math.Min(cutoff.Value, lastDocEtag) > lastProcessedDocEtag)
                        return true;

                    if (DocumentDatabase.DocumentsStorage.GetNumberOfTombstonesWithDocumentEtagLowerThan(databaseContext, collection, cutoff.Value) > 0)
                        return true;
                }
            }

            return false;
        }

        public long GetLastMappedEtagFor(string collection)
        {
            TransactionOperationContext context;
            using (_contextPool.AllocateOperationContext(out context))
            {
                using (var tx = context.OpenReadTransaction())
                {
                    return _indexStorage.ReadLastMappedEtag(tx, collection);
                }
            }
        }

        /// <summary>
        /// This should only be used for testing purposes.
        /// </summary>
        internal Dictionary<string, long> GetLastMappedEtagsForDebug()
        {
            TransactionOperationContext context;
            using (_contextPool.AllocateOperationContext(out context))
            {
                using (var tx = context.OpenReadTransaction())
                {
                    var etags = new Dictionary<string, long>();
                    foreach (var collection in Collections)
                    {
                        etags[collection] = _indexStorage.ReadLastMappedEtag(tx, collection);
                    }

                    return etags;
                }
            }
        }

        protected void ExecuteIndexing()
        {
            using (CultureHelper.EnsureInvariantCulture())
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(DocumentDatabase.DatabaseShutdown, _cancellationTokenSource.Token))
            {
                try
                {
                    DocumentDatabase.Notifications.OnDocumentChange += HandleDocumentChange;

                    while (true)
                    {
                        _indexingInProgress = true;

                        if (Log.IsDebugEnabled)
                            Log.Debug($"Starting indexing for '{Name} ({IndexId})'.");

                        _mre.Reset();

                        var startTime = SystemTime.UtcNow;
                        var stats = new IndexingBatchStats();
                        try
                        {
                            cts.Token.ThrowIfCancellationRequested();

                            DoIndexingWork(stats, cts.Token);

                            _indexingBatchCompleted.SetAndResetAtomically();

                            DocumentDatabase.Notifications.RaiseNotifications(new IndexChangeNotification
                            {
                                Name = Name,
                                Type = IndexChangeTypes.BatchCompleted
                            });

                            ResetWriteErrors();

                            if (Log.IsDebugEnabled)
                                Log.Debug($"Finished indexing for '{Name} ({IndexId})'.'");
                        }
                        catch (OutOfMemoryException oome)
                        {
                            Log.WarnException($"Out of memory occurred for '{Name} ({IndexId})'.", oome);
                            // TODO [ppekrol] GC?
                        }
                        catch (IndexWriteException iwe)
                        {
                            HandleWriteErrors(stats, iwe);
                        }
                        catch (IndexAnalyzerException iae)
                        {
                            stats.AddAnalyzerError(iae);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }
                        catch (Exception e)
                        {
                            Log.WarnException($"Exception occurred for '{Name} ({IndexId})'.", e);
                        }

                        try
                        {
                            _indexStorage.UpdateStats(startTime, stats);
                        }
                        catch (Exception e)
                        {
                            Log.ErrorException($"Could not update stats for '{Name} ({IndexId})'.", e);
                        }

                        _indexingInProgress = false;

                        try
                        {
                            _mre.Wait(cts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }
                    }
                }
                finally
                {
                    DocumentDatabase.Notifications.OnDocumentChange -= HandleDocumentChange;
                }
            }
        }

        internal void ResetWriteErrors()
        {
            writeErrors = Interlocked.Exchange(ref writeErrors, 0);
        }

        internal void HandleWriteErrors(IndexingBatchStats stats, IndexWriteException iwe)
        {
            stats.AddWriteError(iwe);

            if (iwe.InnerException is SystemException) // Don't count transient errors
                return;

            writeErrors = Interlocked.Increment(ref writeErrors);

            if (Priority.HasFlag(IndexingPriority.Error) || Interlocked.Read(ref writeErrors) < WriteErrorsLimit)
                return;

            SetPriority(IndexingPriority.Error);
        }

        protected abstract IIndexingWork[] CreateIndexWorkExecutors();

        public virtual IDisposable InitializeIndexingWork(TransactionOperationContext indexContext)
        {
            return null;
        }

        public void DoIndexingWork(IndexingBatchStats stats, CancellationToken cancellationToken)
        {
            DocumentsOperationContext databaseContext;
            TransactionOperationContext indexContext;

            using (DocumentDatabase.DocumentsStorage.ContextPool.AllocateOperationContext(out databaseContext))
            using (_contextPool.AllocateOperationContext(out indexContext))
            using (var tx = indexContext.OpenWriteTransaction())
            using (InitializeIndexingWork(indexContext))
            {
                var writeOperation = new Lazy<IndexWriteOperation>(() => IndexPersistence.OpenIndexWriter(indexContext.Transaction.InnerTransaction));

                try
                {
                    foreach (var work in _indexWorkers)
                    {
                        var mightBeMore = work.Execute(databaseContext, indexContext, writeOperation, stats, cancellationToken);

                        if (mightBeMore)
                            _mre.Set();
                    }
                }
                finally
                {
                    if (writeOperation.IsValueCreated)
                        writeOperation.Value.Dispose();
                }

                tx.Commit();

                if (writeOperation.IsValueCreated)
                    IndexPersistence.RecreateSearcher(); // we need to recreate it after transaction commit to prevent it from seeing uncommitted changes
            }
        }

        public abstract void HandleDelete(DocumentTombstone tombstone, IndexWriteOperation writer, TransactionOperationContext indexContext);

        public abstract void HandleMap(Document document, IndexWriteOperation writer, TransactionOperationContext indexContext);

        private void HandleIndexChange(IndexChangeNotification notification)
        {
            if (string.Equals(notification.Name, Name, StringComparison.OrdinalIgnoreCase) == false)
                return;

            if (notification.Type == IndexChangeTypes.IndexMarkedAsErrored)
                Stop();
        }

        private void HandleDocumentChange(DocumentChangeNotification notification)
        {
            if (Collections.Contains(notification.CollectionName) == false)
                return;

            _mre.Set();
        }

        public List<IndexingError> GetErrors()
        {
            return _indexStorage.ReadErrors();
        }

        public void SetPriority(IndexingPriority priority)
        {
            if (Priority == priority)
                return;

            lock (_locker)
            {
                if (Priority == priority)
                    return;

                if (Log.IsDebugEnabled)
                    Log.Debug($"Changing priority for '{Name} ({IndexId})' from '{Priority}' to '{priority}'.");

                _indexStorage.WritePriority(priority);

                var oldPriority = Priority;
                Priority = priority;

                var notificationType = IndexChangeTypes.None;

                if (priority.HasFlag(IndexingPriority.Disabled))
                    notificationType = IndexChangeTypes.IndexDemotedToDisabled;
                else if (priority.HasFlag(IndexingPriority.Error))
                    notificationType = IndexChangeTypes.IndexMarkedAsErrored;
                else if (priority.HasFlag(IndexingPriority.Idle))
                    notificationType = IndexChangeTypes.IndexDemotedToIdle;
                else if (priority.HasFlag(IndexingPriority.Normal) && oldPriority.HasFlag(IndexingPriority.Idle))
                    notificationType = IndexChangeTypes.IndexPromotedFromIdle;

                if (notificationType != IndexChangeTypes.None)
                {
                    DocumentDatabase.Notifications.RaiseNotifications(new IndexChangeNotification
                    {
                        Name = Name,
                        Type = notificationType
                    });
                }
            }
        }

        public void SetLock(IndexLockMode mode)
        {
            if (Definition.LockMode == mode)
                return;

            lock (_locker)
            {
                if (Definition.LockMode == mode)
                    return;

                if (Log.IsDebugEnabled)
                    Log.Debug($"Changing lock mode for '{Name} ({IndexId})' from '{Definition.LockMode}' to '{mode}'.");

                _indexStorage.WriteLock(mode);
            }
        }

        public IndexStats GetStats()
        {
            TransactionOperationContext context;
            using (_contextPool.AllocateOperationContext(out context))
            using (var tx = context.OpenReadTransaction())
            {
                return ReadStats(tx);
            }
        }

        private IndexStats ReadStats(RavenTransaction tx)
        {
            using (var reader = IndexPersistence.OpenIndexReader(tx.InnerTransaction))
            {
                var stats = _indexStorage.ReadStats(tx);

                stats.Id = IndexId;
                stats.Name = Name;
                stats.Type = Type;
                stats.ForCollections = Collections.ToArray();
                stats.EntriesCount = reader.EntriesCount();
                stats.LockMode = Definition.LockMode;
                stats.Priority = Priority;

                stats.LastQueryingTime = _lastQueryingTime;

                return stats;
            }
        }

        private void MarkQueried(DateTime time)
        {
            if (_lastQueryingTime != null &&
                _lastQueryingTime.Value >= time)
                return;

            _lastQueryingTime = time;
        }

        public IndexDefinition GetIndexDefinition()
        {
            return Definition.ConvertToIndexDefinition(this);
        }

        public async Task<DocumentQueryResult> Query(IndexQuery query, DocumentsOperationContext documentsContext, CancellationToken token)
        {
            if (_disposed)
                throw new ObjectDisposedException($"Index '{Name} ({IndexId})' was already disposed.");

            if (Priority.HasFlag(IndexingPriority.Idle) && Priority.HasFlag(IndexingPriority.Forced) == false)
                SetPriority(IndexingPriority.Normal);

            MarkQueried(SystemTime.UtcNow);

            var result = new DocumentQueryResult
            {
                IndexName = Name
            };

            TransactionOperationContext indexContext;
            using (_contextPool.AllocateOperationContext(out indexContext))
            {
                var queryDuration = Stopwatch.StartNew();
                AsyncWaitForIndexing wait = null;

                while (true)
                {
                    using (var indexTx = indexContext.OpenReadTransaction())
                    {
                        documentsContext.OpenReadTransaction(); // we have to open read tx for documents _after_ we open index tx

                        if (query.WaitForNonStaleResultsAsOfNow && query.CutoffEtag == null)
                            query.CutoffEtag = Collections.Max(x => DocumentDatabase.DocumentsStorage.GetLastDocumentEtag(documentsContext, x));

                        result.IsStale = IsStale(documentsContext, indexContext, query.CutoffEtag);

                        if (WillResultBeAcceptable(result, query, wait) == false)
                        {
                            documentsContext.Reset();
                            indexContext.Reset();

                            Debug.Assert(query.WaitForNonStaleResultsTimeout != null);

                            if (wait == null)
                                wait = new AsyncWaitForIndexing(queryDuration, query.WaitForNonStaleResultsTimeout.Value, _indexingBatchCompleted);

                            await wait.WaitForIndexingAsync().ConfigureAwait(false);
                            continue;
                        }

                        var stats = ReadStats(indexTx);

                        result.IndexTimestamp = stats.LastIndexingTime ?? DateTime.MinValue;
                        result.LastQueryTime = stats.LastQueryingTime ?? DateTime.MinValue;
                        result.ResultEtag = CalculateIndexEtag(Definition, result.IsStale,
                            lastDocEtags: Collections.Select(x => DocumentDatabase.DocumentsStorage.GetLastDocumentEtag(documentsContext, x)).ToList(),
                            lastMappedEtags: Collections.Select(x => _indexStorage.ReadLastMappedEtag(indexTx, x)).ToList());

                        if (Type == IndexType.MapReduce || Type == IndexType.AutoMapReduce)
                            documentsContext.Reset(); // map reduce don't need to access documents storage

                        using (var reader = IndexPersistence.OpenIndexReader(indexTx.InnerTransaction))
                        {
                            var totalResults = new Reference<int>();

                            result.Results = reader.Query(query, token, totalResults, GetQueryResultRetriever(documentsContext, indexContext)).ToList();
                            result.TotalResults = totalResults.Value;
                        }

                        return result;
                    }
                }
            }
        }

        private static bool WillResultBeAcceptable(DocumentQueryResult result, IndexQuery query, AsyncWaitForIndexing wait)
        {
            if (result.IsStale == false)
                return true;

            if (query.WaitForNonStaleResultsTimeout == null)
                return true;

            if (wait != null && wait.TimeoutExceeded)
                return true;

            return false;
        }

        private static unsafe long CalculateIndexEtag(IndexDefinitionBase definition, bool isStale, List<long> lastDocEtags, List<long> lastMappedEtags)
        {
            var indexEtagBytes = new long[
                1 + // definition hash
                1 + // isStale
                lastDocEtags.Count +
                lastMappedEtags.Count];

            var index = 0;

            indexEtagBytes[index++] = definition.GetHashCode();
            indexEtagBytes[index++] = isStale ? 0L : 1L;

            foreach (var etag in lastDocEtags)
            {
                indexEtagBytes[index++] = etag;
            }

            foreach (var etag in lastMappedEtags)
            {
                indexEtagBytes[index++] = etag;
            }

            // TODO arek - reduce etags
            // TODO arek - index touches?

            unchecked
            {
                fixed (long* buffer = indexEtagBytes)
                {
                    return (long)Hashing.XXHash64.Calculate((byte*)buffer, indexEtagBytes.Length * sizeof(long));
                }
            }
        }

        public long GetIndexEtag()
        {
            DocumentsOperationContext documentContext;
            TransactionOperationContext indexContext;

            using (DocumentDatabase.DocumentsStorage.ContextPool.AllocateOperationContext(out documentContext))
            using (_contextPool.AllocateOperationContext(out indexContext))
            {
                using (var indexTransation = indexContext.OpenReadTransaction())
                using (documentContext.OpenReadTransaction())
                {
                    return CalculateIndexEtag(Definition,
                        IsStale(documentContext, indexContext),
                        lastDocEtags: Collections.Select(x => DocumentDatabase.DocumentsStorage.GetLastDocumentEtag(documentContext, x)).ToList(),
                        lastMappedEtags: Collections.Select(x => _indexStorage.ReadLastMappedEtag(indexTransation, x)).ToList());
                }
            }
        }

        public Dictionary<string, long> GetLastProcessedDocumentTombstonesPerCollection()
        {
            TransactionOperationContext context;
            using (_contextPool.AllocateOperationContext(out context))
            {
                using (var tx = context.OpenReadTransaction())
                {
                    var etags = new Dictionary<string, long>();
                    foreach (var collection in Collections)
                    {
                        etags[collection] = _indexStorage.ReadLastProcessedTombstoneEtag(tx, collection);
                    }

                    return etags;
                }
            }
        }

        public abstract IQueryResultRetriever GetQueryResultRetriever(DocumentsOperationContext documentsContext, TransactionOperationContext indexContext);
    }
}