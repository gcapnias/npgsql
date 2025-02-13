using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Npgsql.BackendMessages;
using Npgsql.Logging;
using NpgsqlTypes;
using static Npgsql.Util.Statics;

namespace Npgsql
{
    /// <summary>
    /// Provides an API for a binary COPY FROM operation, a high-performance data import mechanism to
    /// a PostgreSQL table. Initiated by <see cref="NpgsqlConnection.BeginBinaryImport"/>
    /// </summary>
    /// <remarks>
    /// See http://www.postgresql.org/docs/current/static/sql-copy.html.
    /// </remarks>
    public sealed class NpgsqlBinaryImporter : ICancelable, IAsyncDisposable
    {
        #region Fields and Properties

        NpgsqlConnector _connector;
        NpgsqlWriteBuffer _buf;

        ImporterState _state;

        /// <summary>
        /// The number of columns in the current (not-yet-written) row.
        /// </summary>
        short _column;

        /// <summary>
        /// The number of columns, as returned from the backend in the CopyInResponse.
        /// </summary>
        internal int NumColumns { get; }

        bool InMiddleOfRow => _column != -1 && _column != NumColumns;

        readonly NpgsqlParameter?[] _params;

        static readonly NpgsqlLogger Log = NpgsqlLogManager.CreateLogger(nameof(NpgsqlBinaryImporter));

        #endregion

        #region Construction / Initialization

        internal NpgsqlBinaryImporter(NpgsqlConnector connector, string copyFromCommand)
        {
            _connector = connector;
            _buf = connector.WriteBuffer;
            _column = -1;

            _connector.WriteQuery(copyFromCommand);
            _connector.Flush();

            CopyInResponseMessage copyInResponse;
            var msg = _connector.ReadMessage();
            switch (msg.Code)
            {
                case BackendMessageCode.CopyInResponse:
                    copyInResponse = (CopyInResponseMessage)msg;
                    if (!copyInResponse.IsBinary)
                    {
                        _connector.Break();
                        throw new ArgumentException("copyFromCommand triggered a text transfer, only binary is allowed", nameof(copyFromCommand));
                    }
                    break;
                case BackendMessageCode.CompletedResponse:
                    throw new InvalidOperationException(
                        "This API only supports import/export from the client, i.e. COPY commands containing TO/FROM STDIN. " +
                        "To import/export with files on your PostgreSQL machine, simply execute the command with ExecuteNonQuery. " +
                        "Note that your data has been successfully imported/exported.");
                default:
                    throw _connector.UnexpectedMessageReceived(msg.Code);
            }

            NumColumns = copyInResponse.NumColumns;
            _params = new NpgsqlParameter[NumColumns];
            _buf.StartCopyMode();
            WriteHeader();
        }

        void WriteHeader()
        {
            _buf.WriteBytes(NpgsqlRawCopyStream.BinarySignature, 0, NpgsqlRawCopyStream.BinarySignature.Length);
            _buf.WriteInt32(0);   // Flags field. OID inclusion not supported at the moment.
            _buf.WriteInt32(0);   // Header extension area length
        }

        #endregion

        #region Write

        /// <summary>
        /// Starts writing a single row, must be invoked before writing any columns.
        /// </summary>
        public void StartRow() => StartRow(false).GetAwaiter().GetResult();

        /// <summary>
        /// Starts writing a single row, must be invoked before writing any columns.
        /// </summary>
        public Task StartRowAsync(CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled(cancellationToken);
            using (NoSynchronizationContextScope.Enter())
                return StartRow(true);
        }

        async Task StartRow(bool async)
        {
            CheckReady();

            if (_column != -1 && _column != NumColumns)
                ThrowHelper.ThrowInvalidOperationException_BinaryImportParametersMismatch(NumColumns, _column);

            if (_buf.WriteSpaceLeft < 2)
                await _buf.Flush(async);
            _buf.WriteInt16(NumColumns);

            _column = 0;
        }

        /// <summary>
        /// Writes a single column in the current row.
        /// </summary>
        /// <param name="value">The value to be written</param>
        /// <typeparam name="T">
        /// The type of the column to be written. This must correspond to the actual type or data
        /// corruption will occur. If in doubt, use <see cref="Write{T}(T, NpgsqlDbType)"/> to manually
        /// specify the type.
        /// </typeparam>
        public void Write<T>([AllowNull] T value) => Write(value, false).GetAwaiter().GetResult();

        /// <summary>
        /// Writes a single column in the current row.
        /// </summary>
        /// <param name="value">The value to be written</param>
        /// <param name="cancellationToken"></param>
        /// <typeparam name="T">
        /// The type of the column to be written. This must correspond to the actual type or data
        /// corruption will occur. If in doubt, use <see cref="Write{T}(T, NpgsqlDbType)"/> to manually
        /// specify the type.
        /// </typeparam>
        public Task WriteAsync<T>([AllowNull] T value, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled(cancellationToken);
            using (NoSynchronizationContextScope.Enter())
                return Write(value, true);
        }

        Task Write<T>([AllowNull] T value, bool async)
        {
            CheckColumnIndex();

            var p = _params[_column];
            if (p == null)
            {
                // First row, create the parameter objects
                _params[_column] = p = typeof(T) == typeof(object)
                    ? new NpgsqlParameter()
                    : new NpgsqlParameter<T>();
            }

            return Write(value, p, async);
        }

        /// <summary>
        /// Writes a single column in the current row as type <paramref name="npgsqlDbType"/>.
        /// </summary>
        /// <param name="value">The value to be written</param>
        /// <param name="npgsqlDbType">
        /// In some cases <typeparamref name="T"/> isn't enough to infer the data type to be written to
        /// the database. This parameter and be used to unambiguously specify the type. An example is
        /// the JSONB type, for which <typeparamref name="T"/> will be a simple string but for which
        /// <paramref name="npgsqlDbType"/> must be specified as <see cref="NpgsqlDbType.Jsonb"/>.
        /// </param>
        /// <typeparam name="T">The .NET type of the column to be written.</typeparam>
        public void Write<T>([AllowNull] T value, NpgsqlDbType npgsqlDbType) =>
            Write(value, npgsqlDbType, false).GetAwaiter().GetResult();

        /// <summary>
        /// Writes a single column in the current row as type <paramref name="npgsqlDbType"/>.
        /// </summary>
        /// <param name="value">The value to be written</param>
        /// <param name="npgsqlDbType">
        /// In some cases <typeparamref name="T"/> isn't enough to infer the data type to be written to
        /// the database. This parameter and be used to unambiguously specify the type. An example is
        /// the JSONB type, for which <typeparamref name="T"/> will be a simple string but for which
        /// <paramref name="npgsqlDbType"/> must be specified as <see cref="NpgsqlDbType.Jsonb"/>.
        /// </param>
        /// <param name="cancellationToken"></param>
        /// <typeparam name="T">The .NET type of the column to be written.</typeparam>
        public Task WriteAsync<T>([AllowNull] T value, NpgsqlDbType npgsqlDbType, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled(cancellationToken);
            using (NoSynchronizationContextScope.Enter())
                return Write(value, npgsqlDbType, true);
        }

        Task Write<T>([AllowNull] T value, NpgsqlDbType npgsqlDbType, bool async)
        {
            CheckColumnIndex();

            var p = _params[_column];
            if (p == null)
            {
                // First row, create the parameter objects
                _params[_column] = p = typeof(T) == typeof(object)
                    ? new NpgsqlParameter()
                    : new NpgsqlParameter<T>();
                p.NpgsqlDbType = npgsqlDbType;
            }

            if (npgsqlDbType != p.NpgsqlDbType)
                throw new InvalidOperationException($"Can't change {nameof(p.NpgsqlDbType)} from {p.NpgsqlDbType} to {npgsqlDbType}");

            return Write(value, p, async);
        }

        /// <summary>
        /// Writes a single column in the current row as type <paramref name="dataTypeName"/>.
        /// </summary>
        /// <param name="value">The value to be written</param>
        /// <param name="dataTypeName">
        /// In some cases <typeparamref name="T"/> isn't enough to infer the data type to be written to
        /// the database. This parameter and be used to unambiguously specify the type.
        /// </param>
        /// <typeparam name="T">The .NET type of the column to be written.</typeparam>
        public void Write<T>([AllowNull] T value, string dataTypeName) =>
            Write(value, dataTypeName, false).GetAwaiter().GetResult();

        /// <summary>
        /// Writes a single column in the current row as type <paramref name="dataTypeName"/>.
        /// </summary>
        /// <param name="value">The value to be written</param>
        /// <param name="dataTypeName">
        /// In some cases <typeparamref name="T"/> isn't enough to infer the data type to be written to
        /// the database. This parameter and be used to unambiguously specify the type.
        /// </param>
        /// <param name="cancellationToken"></param>
        /// <typeparam name="T">The .NET type of the column to be written.</typeparam>
        public Task WriteAsync<T>([AllowNull] T value, string dataTypeName, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled(cancellationToken);
            using (NoSynchronizationContextScope.Enter())
                return Write(value, dataTypeName, true);
        }

        Task Write<T>([AllowNull] T value, string dataTypeName, bool async)
        {
            CheckColumnIndex();

            var p = _params[_column];
            if (p == null)
            {
                // First row, create the parameter objects
                _params[_column] = p = typeof(T) == typeof(object)
                    ? new NpgsqlParameter()
                    : new NpgsqlParameter<T>();
                p.DataTypeName = dataTypeName;
            }

            //if (dataTypeName!= p.DataTypeName)
            //    throw new InvalidOperationException($"Can't change {nameof(p.DataTypeName)} from {p.DataTypeName} to {dataTypeName}");

            return Write(value, p, async);
        }

        async Task Write<T>([AllowNull] T value, NpgsqlParameter param, bool async)
        {
            CheckReady();
            if (_column == -1)
                throw new InvalidOperationException("A row hasn't been started");

            if (value == null || value is DBNull)
            {
                await WriteNull(async);
                return;
            }

            if (typeof(T) == typeof(object))
            {
                param.Value = value;
            }
            else
            {
                if (!(param is NpgsqlParameter<T> typedParam))
                {
                    _params[_column] = typedParam = new NpgsqlParameter<T>();
                    typedParam.NpgsqlDbType = param.NpgsqlDbType;
                    param = typedParam;
                }
                typedParam.TypedValue = value;
            }
            param.ResolveHandler(_connector.TypeMapper);
            param.ValidateAndGetLength();
            param.LengthCache?.Rewind();
            await param.WriteWithLength(_buf, async);
            param.LengthCache?.Clear();
            _column++;
        }

        /// <summary>
        /// Writes a single null column value.
        /// </summary>
        public void WriteNull() => WriteNull(false).GetAwaiter().GetResult();

        /// <summary>
        /// Writes a single null column value.
        /// </summary>
        public Task WriteNullAsync(CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled(cancellationToken);
            using (NoSynchronizationContextScope.Enter())
                return WriteNull(true);
        }

        async Task WriteNull(bool async)
        {
            CheckReady();
            if (_column == -1)
                throw new InvalidOperationException("A row hasn't been started");

            if (_buf.WriteSpaceLeft < 4)
                await _buf.Flush(async);

            _buf.WriteInt32(-1);
            _column++;
        }

        /// <summary>
        /// Writes an entire row of columns.
        /// Equivalent to calling <see cref="StartRow()"/>, followed by multiple <see cref="Write{T}(T)"/>
        /// on each value.
        /// </summary>
        /// <param name="values">An array of column values to be written as a single row</param>
        public void WriteRow(params object[] values) => WriteRow(false, values).GetAwaiter().GetResult();

        /// <summary>
        /// Writes an entire row of columns.
        /// Equivalent to calling <see cref="StartRow()"/>, followed by multiple <see cref="Write{T}(T)"/>
        /// on each value.
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <param name="values">An array of column values to be written as a single row</param>
        public Task WriteRowAsync(CancellationToken cancellationToken = default, params object[] values)
        {
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled(cancellationToken);
            using (NoSynchronizationContextScope.Enter())
                return WriteRow(true, values);
        }

        async Task WriteRow(bool async, params object[] values)
        {
            await StartRow(async);
            foreach (var value in values)
                await Write(value, async);
        }

        void CheckColumnIndex()
        {
            if (_column >= NumColumns)
                ThrowHelper.ThrowInvalidOperationException_BinaryImportParametersMismatch(NumColumns, _column + 1);
        }

        #endregion

        #region Commit / Cancel / Close / Dispose

        /// <summary>
        /// Completes the import operation. The writer is unusable after this operation.
        /// </summary>
        public void Complete() => Complete(false).GetAwaiter().GetResult();

        /// <summary>
        /// Completes the import operation. The writer is unusable after this operation.
        /// </summary>
        public ValueTask<ulong> CompleteAsync(CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
                return new ValueTask<ulong>(Task.FromCanceled<ulong>(cancellationToken));
            using (NoSynchronizationContextScope.Enter())
                return Complete(true);
        }

        async ValueTask<ulong> Complete(bool async)
        {
            CheckReady();

            if (InMiddleOfRow)
            {
                await Cancel(async);
                throw new InvalidOperationException("Binary importer closed in the middle of a row, cancelling import.");
            }

            try
            {
                await WriteTrailer(async);
                await _buf.Flush(async);
                _buf.EndCopyMode();
                await _connector.WriteCopyDone(async);
                await _connector.Flush(async);
                var cmdComplete = Expect<CommandCompleteMessage>(await _connector.ReadMessage(async), _connector);
                Expect<ReadyForQueryMessage>(await _connector.ReadMessage(async), _connector);
                _state = ImporterState.Committed;
                return cmdComplete.Rows;
            }
            catch
            {
                // An exception here will have already broken the connection etc.
                Cleanup();
                throw;
            }
        }

        void ICancelable.Cancel() => Close();

        /// <summary>
        /// Cancels that binary import and sets the connection back to idle state
        /// </summary>
        public void Dispose() => Close();

        /// <summary>
        /// Async cancels that binary import and sets the connection back to idle state
        /// </summary>
        /// <returns></returns>
        public ValueTask DisposeAsync()
        {
            using (NoSynchronizationContextScope.Enter())
                return CloseAsync(true);
        }

        async Task Cancel(bool async)
        {
            _state = ImporterState.Cancelled;
            _buf.Clear();
            _buf.EndCopyMode();
            await _connector.WriteCopyFail(async);
            await _connector.Flush(async);
            try
            {
                var msg = await _connector.ReadMessage(async);
                // The CopyFail should immediately trigger an exception from the read above.
                _connector.Break();
                throw new NpgsqlException("Expected ErrorResponse when cancelling COPY but got: " + msg.Code);
            }
            catch (PostgresException e)
            {
                if (e.SqlState != PostgresErrorCodes.QueryCanceled)
                    throw;
            }
        }

        /// <summary>
        /// Completes the import process and signals to the database to write everything.
        /// </summary>
        [PublicAPI]
        public void Close() => CloseAsync(false).GetAwaiter().GetResult();

        /// <summary>
        /// Async completes the import process and signals to the database to write everything.
        /// </summary>
        /// <returns></returns>
        /// <exception cref="Exception"></exception>
        [PublicAPI]
        public ValueTask CloseAsync(CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
                return new ValueTask(Task.FromCanceled(cancellationToken));
            using (NoSynchronizationContextScope.Enter())
                return CloseAsync(true);
        }

        async ValueTask CloseAsync(bool async)
        {
            switch (_state)
            {
            case ImporterState.Disposed:
                return;
            case ImporterState.Ready:
                await Cancel(async);
                break;
            case ImporterState.Cancelled:
            case ImporterState.Committed:
                break;
            default:
                throw new Exception("Invalid state: " + _state);
            }

            var connector = _connector;
            Cleanup();
            connector.EndUserAction();
        }

#pragma warning disable CS8625
        void Cleanup()
        {
            var connector = _connector;
            Log.Debug("COPY operation ended", connector?.Id ?? -1);

            if (connector != null)
            {
                connector.CurrentCopyOperation = null;
                _connector = null;
            }

            _buf = null;
            _state = ImporterState.Disposed;
        }
#pragma warning restore CS8625

        async Task WriteTrailer(bool async)
        {
            if (_buf.WriteSpaceLeft < 2)
                await _buf.Flush(async);
            _buf.WriteInt16(-1);
        }

        void CheckReady()
        {
            switch (_state)
            {
            case ImporterState.Ready:
                return;
            case ImporterState.Disposed:
                throw new ObjectDisposedException(GetType().FullName, "The COPY operation has already ended.");
            case ImporterState.Cancelled:
                throw new InvalidOperationException("The COPY operation has already been cancelled.");
            case ImporterState.Committed:
                throw new InvalidOperationException("The COPY operation has already been committed.");
            default:
                throw new Exception("Invalid state: " + _state);
            }
        }

        #endregion

        #region Enums

        enum ImporterState
        {
            Ready,
            Committed,
            Cancelled,
            Disposed
        }

        #endregion Enums
    }
}
