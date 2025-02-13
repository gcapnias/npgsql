using System;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Npgsql.Logging;

namespace Npgsql
{
    /// <summary>
    /// Represents a transaction to be made in a PostgreSQL database. This class cannot be inherited.
    /// </summary>
    public sealed class NpgsqlTransaction : DbTransaction
    {
        #region Fields and Properties

        /// <summary>
        /// Specifies the <see cref="NpgsqlConnection"/> object associated with the transaction.
        /// </summary>
        /// <value>The <see cref="NpgsqlConnection"/> object associated with the transaction.</value>
        public new NpgsqlConnection Connection { get; internal set; }

        // Note that with ambient transactions, it's possible for a transaction to be pending after its connection
        // is already closed. So we capture the connector and perform everything directly on it.
        NpgsqlConnector _connector;

        /// <summary>
        /// Specifies the completion state of the transaction.
        /// </summary>
        /// <value>The completion state of the transaction.</value>
        public bool IsCompleted => _connector == null;

        /// <summary>
        /// Specifies the <see cref="NpgsqlConnection"/> object associated with the transaction.
        /// </summary>
        /// <value>The <see cref="NpgsqlConnection"/> object associated with the transaction.</value>
        protected override DbConnection DbConnection => Connection;

        bool _isDisposed;

        /// <summary>
        /// Specifies the <see cref="System.Data.IsolationLevel">IsolationLevel</see> for this transaction.
        /// </summary>
        /// <value>The <see cref="System.Data.IsolationLevel">IsolationLevel</see> for this transaction.
        /// The default is <b>ReadCommitted</b>.</value>
        public override IsolationLevel IsolationLevel
        {
            get
            {
                CheckReady();
                return _isolationLevel;
            }
        }
        readonly IsolationLevel _isolationLevel;

        static readonly NpgsqlLogger Log = NpgsqlLogManager.CreateLogger(nameof(NpgsqlTransaction));

        const IsolationLevel DefaultIsolationLevel = IsolationLevel.ReadCommitted;

        #endregion

        #region Constructors

        internal NpgsqlTransaction(NpgsqlConnection conn, IsolationLevel isolationLevel = DefaultIsolationLevel)
        {
            Debug.Assert(isolationLevel != IsolationLevel.Chaos);

            Connection = conn;
            _connector = Connection.CheckReadyAndGetConnector();

            if (!_connector.DatabaseInfo.SupportsTransactions)
                return;

            Log.Debug($"Beginning transaction with isolation level {isolationLevel}", _connector.Id);
            _connector.Transaction = this;
            _connector.TransactionStatus = TransactionStatus.Pending;

            switch (isolationLevel) {
                case IsolationLevel.RepeatableRead:
                case IsolationLevel.Snapshot:
                    _connector.PrependInternalMessage(PregeneratedMessages.BeginTransRepeatableRead, 2);
                    break;
                case IsolationLevel.Serializable:
                    _connector.PrependInternalMessage(PregeneratedMessages.BeginTransSerializable, 2);
                    break;
                case IsolationLevel.ReadUncommitted:
                    // PG doesn't really support ReadUncommitted, it's the same as ReadCommitted. But we still
                    // send as if.
                    _connector.PrependInternalMessage(PregeneratedMessages.BeginTransReadUncommitted, 2);
                    break;
                case IsolationLevel.ReadCommitted:
                    _connector.PrependInternalMessage(PregeneratedMessages.BeginTransReadCommitted, 2);
                    break;
                case IsolationLevel.Unspecified:
                    isolationLevel = DefaultIsolationLevel;
                    goto case DefaultIsolationLevel;
                default:
                    throw new NotSupportedException("Isolation level not supported: " + isolationLevel);
            }

            _isolationLevel = isolationLevel;
        }

        #endregion

        #region Commit

        /// <summary>
        /// Commits the database transaction.
        /// </summary>
        public override void Commit() => Commit(false).GetAwaiter().GetResult();

        async Task Commit(bool async)
        {
            CheckReady();

            if (!_connector.DatabaseInfo.SupportsTransactions)
                return;

            using (_connector.StartUserAction())
            {
                Log.Debug("Committing transaction", _connector.Id);
                await _connector.ExecuteInternalCommand(PregeneratedMessages.CommitTransaction, async);
                Clear();
            }
        }

        /// <summary>
        /// Commits the database transaction.
        /// </summary>
        /// <param name="cancellationToken">The token to monitor for cancellation requests. The default value is <see cref="CancellationToken.None"/>.</param>
#if !NET461 && !NETSTANDARD2_0
        public override Task CommitAsync(CancellationToken cancellationToken = default)
#else
        public Task CommitAsync(CancellationToken cancellationToken = default)
#endif
        {
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled(cancellationToken);
            using (NoSynchronizationContextScope.Enter())
                return Commit(true);
        }

        // This overload exists only to avoid introducing a binary breaking change in 4.1. Removed in 5.0.
        /// <summary>
        /// Commits the database transaction.
        /// </summary>
        public Task CommitAsync() => CommitAsync(CancellationToken.None);

        #endregion

        #region Rollback

        /// <summary>
        /// Rolls back a transaction from a pending state.
        /// </summary>
        public override void Rollback() => Rollback(false).GetAwaiter().GetResult();

        async Task Rollback(bool async)
        {
            CheckReady();
            if (!_connector.DatabaseInfo.SupportsTransactions)
                return;
            await _connector.Rollback(async);
            Clear();
        }

        /// <summary>
        /// Rolls back a transaction from a pending state.
        /// </summary>
        /// <param name="cancellationToken">The token to monitor for cancellation requests. The default value is <see cref="CancellationToken.None"/>.</param>
#if !NET461 && !NETSTANDARD2_0
        public override Task RollbackAsync(CancellationToken cancellationToken = default)
#else
        public Task RollbackAsync(CancellationToken cancellationToken = default)
#endif
        {
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled(cancellationToken);
            using (NoSynchronizationContextScope.Enter())
                return Rollback(true);
        }

        // This overload exists only to avoid introducing a binary breaking change in 4.1. Removed in 5.0.
        /// <summary>
        /// Rolls back a transaction from a pending state.
        /// </summary>
        public Task RollbackAsync() => RollbackAsync(CancellationToken.None);

        #endregion

        #region Savepoints

        async Task Save(string name, bool async)
        {
            if (name == null)
                throw new ArgumentNullException(nameof(name));
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("name can't be empty", nameof(name));
            if (name.Contains(";"))
                throw new ArgumentException("name can't contain a semicolon");

            CheckReady();
            if (!_connector.DatabaseInfo.SupportsTransactions)
                return;
            using (_connector.StartUserAction())
            {
                Log.Debug($"Creating savepoint {name}", _connector.Id);
                await _connector.ExecuteInternalCommand($"SAVEPOINT {name}", async);
            }
        }

        /// <summary>
        /// Creates a transaction save point.
        /// </summary>
        /// <param name="name">The name of the savepoint.</param>
        public void Save(string name) => Save(name, false).GetAwaiter().GetResult();

        /// <summary>
        /// Creates a transaction save point.
        /// </summary>
        /// <param name="name">The name of the savepoint.</param>
        /// <param name="cancellationToken">The token to monitor for cancellation requests. The default value is <see cref="CancellationToken.None"/>.</param>
        public Task SaveAsync(string name, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled(cancellationToken);
            using (NoSynchronizationContextScope.Enter())
                return Save(name, true);
        }

        async Task Rollback(string name, bool async)
        {
            if (name == null)
                throw new ArgumentNullException(nameof(name));
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("name can't be empty", nameof(name));
            if (name.Contains(";"))
                throw new ArgumentException("name can't contain a semicolon");

            CheckReady();
            if (!_connector.DatabaseInfo.SupportsTransactions)
                return;
            using (_connector.StartUserAction())
            {
                Log.Debug($"Rolling back savepoint {name}", _connector.Id);
                await _connector.ExecuteInternalCommand($"ROLLBACK TO SAVEPOINT {name}", async);
            }
        }

        /// <summary>
        /// Rolls back a transaction from a pending savepoint state.
        /// </summary>
        /// <param name="name">The name of the savepoint.</param>
        public void Rollback(string name) => Rollback(name, false).GetAwaiter().GetResult();

        /// <summary>
        /// Rolls back a transaction from a pending savepoint state.
        /// </summary>
        /// <param name="name">The name of the savepoint.</param>
        /// <param name="cancellationToken">The token to monitor for cancellation requests. The default value is <see cref="CancellationToken.None"/>.</param>
        public Task RollbackAsync(string name, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled(cancellationToken);
            using (NoSynchronizationContextScope.Enter())
                return Rollback(name, true);
        }

        async Task Release(string name, bool async)
        {
            if (name == null)
                throw new ArgumentNullException(nameof(name));
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("name can't be empty", nameof(name));
            if (name.Contains(";"))
                throw new ArgumentException("name can't contain a semicolon");

            CheckReady();
            if (!_connector.DatabaseInfo.SupportsTransactions)
                return;
            using (_connector.StartUserAction())
            {
                Log.Debug($"Releasing savepoint {name}", _connector.Id);
                await _connector.ExecuteInternalCommand($"RELEASE SAVEPOINT {name}", async);
            }
        }

        /// <summary>
        /// Releases a transaction from a pending savepoint state.
        /// </summary>
        /// <param name="name">The name of the savepoint.</param>
        public void Release(string name) => Release(name, false).GetAwaiter().GetResult();

        /// <summary>
        /// Releases a transaction from a pending savepoint state.
        /// </summary>
        /// <param name="name">The name of the savepoint.</param>
        /// <param name="cancellationToken">The token to monitor for cancellation requests. The default value is <see cref="CancellationToken.None"/>.</param>
        public Task ReleaseAsync(string name, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled(cancellationToken);
            using (NoSynchronizationContextScope.Enter())
                return Release(name, true);
        }

        #endregion

        #region Dispose

        /// <summary>
        /// Disposes the transaction, rolling it back if it is still pending.
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (_isDisposed)
                return;

            if (disposing && !IsCompleted)
            {
                try
                {
                    _connector.CloseOngoingOperations(async: false).GetAwaiter().GetResult();
                    Rollback();
                }
                catch (Exception ex)
                {
                    Debug.Assert(_connector.IsBroken);
                    Log.Error("Exception while disposing a transaction", ex, _connector.Id);
                }
            }

            Clear();

            base.Dispose(disposing);
            _isDisposed = true;
        }

        /// <summary>
        /// Disposes the transaction, rolling it back if it is still pending.
        /// </summary>
#if !NET461 && !NETSTANDARD2_0
        public override ValueTask DisposeAsync()
#else
        public ValueTask DisposeAsync()
#endif
        {
            if (!_isDisposed)
            {
                if (!IsCompleted)
                {
                    using (NoSynchronizationContextScope.Enter())
                        return DisposeAsyncInternal();
                }

                _isDisposed = true;
            }
            return default;

            async ValueTask DisposeAsyncInternal()
            {
            	try
            	{
                	await _connector.CloseOngoingOperations(async: true);
                	await Rollback(async: true);
            	}
            	catch (Exception ex)
            	{
            		Debug.Assert(_connector.IsBroken);
                    Log.Error("Exception while disposing a transaction", ex, _connector.Id);
            	}
                _isDisposed = true;
            }
        }

#pragma warning disable CS8625
        internal void Clear()
        {
            _connector = null;
            Connection = null;
        }
#pragma warning restore CS8625

        #endregion

        #region Checks

        void CheckReady()
        {
            CheckDisposed();
            CheckCompleted();
        }

        void CheckCompleted()
        {
            if (IsCompleted)
                throw new InvalidOperationException("This NpgsqlTransaction has completed; it is no longer usable.");
        }

        void CheckDisposed()
        {
            if (_isDisposed)
                throw new ObjectDisposedException(typeof(NpgsqlTransaction).Name);
        }

        #endregion
    }
}
