using Microsoft.Data.Sqlite;
using SQLitePCL;
using Xakpc.SQLiteMCPSidecar.Configuration;

namespace Xakpc.SQLiteMCPSidecar.Database;

/// <summary>
/// The authorizer policy of one operation. The policy comes from the tool that runs, never from the
/// deployment permission set.
/// </summary>
internal enum AuthorizerPolicy
{
    /// <summary>Read-only caller SQL. The policy of the <c>query</c> tool.</summary>
    Read,

    /// <summary>
    /// Server-authored DML. The policy of the structured write tools, <c>insert</c>, <c>update</c>
    /// and <c>delete</c>.
    /// </summary>
    Write,

    /// <summary>
    /// Caller-supplied DML. The policy of <c>execute_write_sql</c>, and the only policy of the three
    /// that ever sees caller SQL on a write connection.
    /// </summary>
    Dml,
}

/// <summary>The outcome of the one-statement check that runs before execution.</summary>
internal enum StatementCheck
{
    /// <summary>Exactly one statement, and the authorizer accepted it.</summary>
    Ok,

    /// <summary>The text holds no statement.</summary>
    Empty,

    /// <summary>The text holds more than one statement.</summary>
    MultipleStatements,

    /// <summary>The authorizer rejected an action during preparation.</summary>
    Rejected,

    /// <summary>
    /// The statement writes nothing, on a path that exists to write. It belongs in the query tool.
    /// </summary>
    /// <remarks>
    /// The authorizer cannot produce this: <see cref="AuthorizerPolicy.Dml"/> has to accept read
    /// actions, because a <c>WHERE</c> clause, a subquery and <c>INSERT ... SELECT</c> all read. It
    /// sees actions and never statement kinds, thus the kind is asked of the prepared statement.
    /// </remarks>
    ReadOnlyOnAWritePath,

    /// <summary>The statement did not prepare: malformed SQL, an unknown table, or a limit.</summary>
    Invalid,

    /// <summary>
    /// Another connection holds the lock and the busy timeout expired. The statement itself is
    /// correct, thus this is not <see cref="Invalid"/>: the agent retries instead of rewriting it.
    /// </summary>
    Busy,
}

/// <summary>
/// The SQLite sandbox. It is the innermost security layer, it is always on, and no permission
/// disables any part of it. <c>danger-raw-write</c> does not weaken it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Microsoft.Data.Sqlite"/> exposes none of these controls, but it does expose the raw
/// handle, and <c>SQLitePCLRaw.core</c> exposes the native functions. The transitive reference is
/// enough.
/// </para>
/// <para>
/// <b>Invariant.</b> Apply the baseline after every <c>Open</c>, never one time at startup. These
/// settings live on the connection handle and not on the process. <c>Pooling=false</c> is what makes
/// that safe: a pooled handle would keep the authorizer of an earlier operation, which is a
/// privilege-escalation path.
/// </para>
/// </remarks>
internal static class SqliteSecurity
{
    /// <summary>
    /// Runtime limits that constrain untrusted SQL. Only the SQL length comes from configuration:
    /// the others are product constants, because an operator has no reason to raise them and a
    /// configuration value is a way to weaken the sandbox by accident.
    /// </summary>
    /// <remarks>
    /// <b>Lesson.</b> <c>SQLITE_LIMIT_VDBE_OP</c> bounds the number of instructions in the prepared
    /// program, which is statement complexity and not work done. A recursive CTE is a short program
    /// that runs for ever. The runaway stop is the timeout with <c>sqlite3_interrupt</c>, and the
    /// progress handler is the second path. See <see cref="RegisterInterrupt"/>.
    /// </remarks>
    private const int MaxColumns = 512;
    private const int MaxExpressionDepth = 100;
    private const int MaxCompoundSelect = 10;
    private const int MaxVariableNumber = 100;
    private const int MaxLikePatternLength = 1000;
    private const int MaxVdbeOperations = 100_000;

    /// <summary>VM steps between two progress callbacks. Small enough to stop a runaway quickly.</summary>
    private const int ProgressHandlerInterval = 1000;

    /// <summary>
    /// SQL functions that the authorizer rejects by name. Extension loading is also off at the
    /// connection level, thus this is the second block and not the only one.
    /// </summary>
    private static readonly string[] DeniedFunctions = ["load_extension", "fts3_tokenizer"];

    /// <summary>
    /// Applies the always-on controls. Call it immediately after <c>Open</c> and before any
    /// statement runs.
    /// </summary>
    public static void ApplyBaseline(SqliteConnection connection, SidecarOptions options)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(options);

        var handle = connection.Handle
            ?? throw new InvalidOperationException("The connection is not open, thus it has no handle.");

        // Defensive mode blocks direct schema corruption. Trusted schema off distrusts the objects
        // that the schema itself stores, for example a view that calls a dangerous function.
        Check(raw.sqlite3_db_config(handle, raw.SQLITE_DBCONFIG_DEFENSIVE, 1, out _), handle, "defensive mode");
        Check(raw.sqlite3_db_config(handle, raw.SQLITE_DBCONFIG_TRUSTED_SCHEMA, 0, out _), handle, "trusted schema");

        // Extension loading is arbitrary native code inside the sidecar process. No permission
        // enables it. Off is already the default, and the explicit call keeps that visible.
        connection.EnableExtensions(false);

        // A second, independent block on ATTACH next to the authorizer rule.
        raw.sqlite3_limit(handle, raw.SQLITE_LIMIT_ATTACHED, 0);

        raw.sqlite3_limit(handle, raw.SQLITE_LIMIT_SQL_LENGTH, options.MaxSqlBytes);
        raw.sqlite3_limit(handle, raw.SQLITE_LIMIT_COLUMN, MaxColumns);
        raw.sqlite3_limit(handle, raw.SQLITE_LIMIT_EXPR_DEPTH, MaxExpressionDepth);
        raw.sqlite3_limit(handle, raw.SQLITE_LIMIT_COMPOUND_SELECT, MaxCompoundSelect);
        raw.sqlite3_limit(handle, raw.SQLITE_LIMIT_VARIABLE_NUMBER, MaxVariableNumber);
        raw.sqlite3_limit(handle, raw.SQLITE_LIMIT_LIKE_PATTERN_LENGTH, MaxLikePatternLength);
        raw.sqlite3_limit(handle, raw.SQLITE_LIMIT_VDBE_OP, MaxVdbeOperations);

        // The busy timeout belongs on the handle, because the sidecar does not reach SQLite through
        // Microsoft.Data.Sqlite alone. ValidateSingleStatement calls sqlite3_prepare_v2 directly,
        // thus the DefaultTimeout retry loop of the provider never sees that call: without this
        // line a prepare against a locked database fails in milliseconds and the configured
        // SQLITE_SIDECAR_BUSY_TIMEOUT_SECONDS does nothing on the read path.
        //
        // The provider keeps its own retry loop on top of this one. They compose: SQLite waits
        // inside the call and the provider retries around it, thus the handle-level wait is what
        // the agent actually gets.
        raw.sqlite3_busy_timeout(handle, options.BusyTimeoutSeconds * 1000);
    }

    /// <summary>
    /// Installs the authorizer for one operation. Dispose removes it.
    /// </summary>
    /// <remarks>
    /// <b>Invariant.</b> Install the authorizer after every server-authored statement. The sidecar
    /// runs its own <c>PRAGMA query_only=ON</c>, and each policy rejects <c>PRAGMA</c>. The same
    /// ordering rule covers <c>BEGIN IMMEDIATE</c> on the write path later.
    /// </remarks>
    public static IDisposable InstallAuthorizer(SqliteConnection connection, AuthorizerPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var handle = connection.Handle
            ?? throw new InvalidOperationException("The connection is not open, thus it has no handle.");

        return new AuthorizerScope(handle, policy);
    }

    /// <summary>
    /// Makes cancellation stop SQLite itself, and not only the caller that waits.
    /// </summary>
    /// <remarks>
    /// Two stop paths. <c>sqlite3_interrupt</c> fires on the token, and the progress handler reports
    /// the cancellation from inside the virtual machine, which also bounds a statement that never
    /// yields to the token registration.
    /// </remarks>
    public static IDisposable RegisterInterrupt(SqliteConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var handle = connection.Handle
            ?? throw new InvalidOperationException("The connection is not open, thus it has no handle.");

        return new InterruptScope(handle, cancellationToken);
    }

    /// <summary>
    /// Proves that the caller sent exactly one statement, and that the authorizer accepts it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The count comes from preparation and never from counting semicolons: a semicolon appears in a
    /// string literal and in a comment. <c>sqlite3_prepare_v2</c> reports the unconsumed text in
    /// <c>tail</c>, thus a non-empty tail is a second statement.
    /// </para>
    /// <para>
    /// <paramref name="mustWrite"/> is <c>true</c> on the raw write path. It is the only way to tell
    /// a bare <c>SELECT</c> from the read half of a write, because the authorizer sees actions and
    /// not statement kinds.
    /// </para>
    /// </remarks>
    public static StatementCheck ValidateSingleStatement(
        SqliteConnection connection,
        string sql,
        bool mustWrite = false)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(sql);

        var handle = connection.Handle
            ?? throw new InvalidOperationException("The connection is not open, thus it has no handle.");

        sqlite3_stmt? statement = null;
        try
        {
            var result = raw.sqlite3_prepare_v2(handle, sql, out statement, out var tail);
            if (result == raw.SQLITE_AUTH)
            {
                return StatementCheck.Rejected;
            }

            // A lock is not a syntax error. The owning application writes while the sidecar reads,
            // thus a busy database is an ordinary condition of this product and not a caller
            // mistake. Reporting it as Invalid would tell the agent to rewrite a correct statement.
            if (result is raw.SQLITE_BUSY or raw.SQLITE_LOCKED)
            {
                return StatementCheck.Busy;
            }

            if (result != raw.SQLITE_OK)
            {
                return StatementCheck.Invalid;
            }

            // Whitespace or a comment only. SQLite prepares nothing and reports no error.
            //
            // LESSON: `statement is null` is NOT the test. SQLitePCLRaw hands back a non-null
            // sqlite3_stmt wrapper around a null pointer for empty input, thus a null check is dead
            // code and every empty statement was reported as a successful query of zero rows. The
            // agent then read "the table is empty" where "you sent no statement" was the truth. Ask
            // the statement for its own text instead: an empty one has none.
            if (statement is null || string.IsNullOrWhiteSpace(raw.sqlite3_sql(statement).utf8_to_string()))
            {
                return StatementCheck.Empty;
            }

            if (!string.IsNullOrWhiteSpace(tail))
            {
                return StatementCheck.MultipleStatements;
            }

            // A statement that writes nothing has no business on the write path. It would otherwise
            // run inside BEGIN IMMEDIATE and hold the write lock of the owning application for its
            // whole duration, while the query tool runs the same text on a read-only connection and
            // blocks nobody.
            if (mustWrite && raw.sqlite3_stmt_readonly(statement) != 0)
            {
                return StatementCheck.ReadOnlyOnAWritePath;
            }

            return StatementCheck.Ok;
        }
        finally
        {
            if (statement is not null)
            {
                raw.sqlite3_finalize(statement);
            }
        }
    }

    /// <summary>
    /// Reads the rowid of the last inserted row on this connection.
    /// </summary>
    /// <remarks>
    /// <c>Microsoft.Data.Sqlite</c> exposes no such property, and <c>SELECT last_insert_rowid()</c>
    /// would cost one more statement inside the transaction. The native call reads connection state and
    /// runs no statement, thus it also works while an authorizer is installed.
    /// </remarks>
    public static long LastInsertRowId(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var handle = connection.Handle
            ?? throw new InvalidOperationException("The connection is not open, thus it has no handle.");

        return raw.sqlite3_last_insert_rowid(handle);
    }

    /// <summary>
    /// Reads the number of rows that every statement on this connection has changed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Invariant.</b> This is the count that bounds a structured write, and not the count that
    /// <c>ExecuteNonQuery</c> returns. <c>ExecuteNonQuery</c> reports the rows of the target table
    /// only, while this counter also includes the rows that an <c>ON DELETE CASCADE</c> removes and
    /// the rows that a trigger writes. Those rows are real damage, and the connection sets
    /// <c>ForeignKeys = true</c>, thus a <c>delete</c> with <c>maxRows = 1</c> would otherwise destroy
    /// a whole subtree and report one row. See
    /// <c>.lode/decisions/0004-maxrows-bounds-total-changes.md</c>.
    /// </para>
    /// <para>
    /// The value is cumulative for the lifetime of the connection, thus a caller uses the difference
    /// across one statement. Pooling is off and a write opens its own connection, so that difference
    /// belongs to the one statement that ran.
    /// </para>
    /// <para>
    /// Like <see cref="LastInsertRowId"/> it reads connection state and runs no statement, thus it
    /// also works while an authorizer is installed.
    /// </para>
    /// </remarks>
    public static int TotalChanges(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var handle = connection.Handle
            ?? throw new InvalidOperationException("The connection is not open, thus it has no handle.");

        return raw.sqlite3_total_changes(handle);
    }

    /// <summary>
    /// Reads the rows that the most recent statement on this connection changed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the <c>rowsAffected</c> number: the rows of the target table, with no cascaded row and
    /// no row that a trigger wrote. Use <see cref="TotalChanges"/> for the blast radius, which is what
    /// bounds a structured write and what the write budget counts.
    /// </para>
    /// <para>
    /// A raw write runs through a reader, because a <c>RETURNING</c> clause produces rows, thus the
    /// <c>ExecuteNonQuery</c> return value is not available. The native counter is. Like
    /// <see cref="TotalChanges"/> it reads connection state and runs no statement. Read it after the
    /// statement ran to completion: SQLite sets the counter when the statement finishes.
    /// </para>
    /// </remarks>
    public static int Changes(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var handle = connection.Handle
            ?? throw new InvalidOperationException("The connection is not open, thus it has no handle.");

        return raw.sqlite3_changes(handle);
    }

    private static void Check(int result, sqlite3 handle, string what)
    {
        if (result != raw.SQLITE_OK)
        {
            // The message names the control and not the database file. It reaches the log only.
            throw new InvalidOperationException(
                $"The sandbox could not set {what}. SQLite returned {result}: {raw.sqlite3_errmsg(handle).utf8_to_string()}");
        }
    }

    /// <summary>
    /// Holds the native authorizer callback for the whole lifetime of the operation.
    /// </summary>
    /// <remarks>
    /// <b>Lesson.</b> The callback is a native callback. A collected delegate is a hard process
    /// crash and not an exception, thus the field keeps it reachable until <see cref="Dispose"/>.
    /// </remarks>
    private sealed class AuthorizerScope : IDisposable
    {
        private readonly sqlite3 _handle;
        private readonly delegate_authorizer _callback;
        private bool _removed;

        public AuthorizerScope(sqlite3 handle, AuthorizerPolicy policy)
        {
            _handle = handle;
            _callback = policy switch
            {
                AuthorizerPolicy.Read => ReadPolicy,
                AuthorizerPolicy.Write => WritePolicy,
                AuthorizerPolicy.Dml => DmlPolicy,
                _ => throw new ArgumentOutOfRangeException(nameof(policy)),
            };

            var result = raw.sqlite3_set_authorizer(_handle, _callback, null);
            if (result != raw.SQLITE_OK)
            {
                throw new InvalidOperationException($"The authorizer could not be installed. SQLite returned {result}.");
            }
        }

        public void Dispose()
        {
            if (_removed)
            {
                return;
            }

            _removed = true;
            raw.sqlite3_set_authorizer(_handle, (delegate_authorizer?)null, null);
            GC.KeepAlive(_callback);
        }

        /// <summary>
        /// The read policy. It is an allowlist: it accepts a named few actions and rejects each
        /// other one.
        /// </summary>
        /// <remarks>
        /// An allowlist and not a denylist. SQLite has more than thirty action codes, a denylist
        /// admits without a word any code that a later SQLite version adds, and the code that
        /// <c>VACUUM INTO</c> reports is not dependable. An allowlist rejects each of them with no
        /// need to name them.
        /// </remarks>
        private static int ReadPolicy(object? userData, int actionCode, utf8z arg1, utf8z arg2, utf8z dbName, utf8z trigger)
        {
            try
            {
                if (actionCode == raw.SQLITE_SELECT || actionCode == raw.SQLITE_READ || actionCode == raw.SQLITE_RECURSIVE)
                {
                    return raw.SQLITE_OK;
                }

                if (actionCode == raw.SQLITE_FUNCTION)
                {
                    // For SQLITE_FUNCTION the first argument is absent and the second is the name.
                    var name = arg2.utf8_to_string();
                    return name is not null && !IsDeniedFunction(name) ? raw.SQLITE_OK : raw.SQLITE_DENY;
                }

                return raw.SQLITE_DENY;
            }
            catch
            {
                // An exception must never cross a native callback boundary. Reject instead.
                return raw.SQLITE_DENY;
            }
        }

        /// <summary>
        /// The write policy. It is the read policy plus the three DML actions.
        /// </summary>
        /// <remarks>
        /// <para>
        /// An allowlist for the same three reasons as the read policy. <c>SQLITE_TRANSACTION</c> stays
        /// denied, thus the server must run <c>BEGIN IMMEDIATE</c> before it installs this policy and
        /// must remove the policy before it commits. <c>SQLITE_PRAGMA</c> also stays denied, thus the
        /// identifier validation runs before the installation.
        /// </para>
        /// <para>
        /// <c>SQLITE_SELECT</c> and <c>SQLITE_READ</c> are necessary and they are not a weakness. The
        /// bounded pre-count is a <c>SELECT</c>, a <c>CHECK</c> constraint reads the new row, and a
        /// foreign key reads the referenced table. The statement is server-authored in each case, thus
        /// this policy never sees caller SQL. <see cref="DmlPolicy"/> is the one that does.
        /// </para>
        /// </remarks>
        private static int WritePolicy(object? userData, int actionCode, utf8z arg1, utf8z arg2, utf8z dbName, utf8z trigger)
        {
            try
            {
                if (actionCode == raw.SQLITE_INSERT || actionCode == raw.SQLITE_UPDATE || actionCode == raw.SQLITE_DELETE)
                {
                    return raw.SQLITE_OK;
                }

                if (actionCode == raw.SQLITE_SELECT || actionCode == raw.SQLITE_READ || actionCode == raw.SQLITE_RECURSIVE)
                {
                    return raw.SQLITE_OK;
                }

                if (actionCode == raw.SQLITE_FUNCTION)
                {
                    var name = arg2.utf8_to_string();
                    return name is not null && !IsDeniedFunction(name) ? raw.SQLITE_OK : raw.SQLITE_DENY;
                }

                return raw.SQLITE_DENY;
            }
            catch
            {
                // An exception must never cross a native callback boundary. Reject instead.
                return raw.SQLITE_DENY;
            }
        }

        /// <summary>
        /// The DML policy. It is the write policy plus one rule: a write to an <c>sqlite_%</c> object
        /// is rejected.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the one policy that sees caller SQL on a write connection. It accepts the three DML
        /// actions and it rejects each other one, thus <c>danger-raw-write</c> is raw
        /// <c>INSERT</c> / <c>UPDATE</c> / <c>DELETE</c> inside the sandbox and not unrestricted
        /// SQLite. DDL, <c>ATTACH</c>, <c>PRAGMA</c>, <c>VACUUM</c> and transaction control all stay
        /// denied, exactly as they are for the <c>query</c> tool.
        /// </para>
        /// <para>
        /// <b>The one difference from <see cref="WritePolicy"/>.</b> For a DML action the first
        /// callback argument is the table name, and a name that starts with <c>sqlite_</c> is SQLite
        /// internal state. <c>UPDATE sqlite_sequence</c> would change the autoincrement behaviour of
        /// the owning application, which is not data manipulation of the caller's own rows.
        /// <c>StructuredWriteBuilder</c> already refuses such a target by name, thus this rule gives the
        /// raw path the same boundary one layer lower.
        /// </para>
        /// <para>
        /// <c>SQLITE_SELECT</c> and <c>SQLITE_READ</c> stay accepted. An <c>INSERT ... SELECT</c>, a
        /// <c>WHERE</c> clause, a <c>CHECK</c> constraint and a foreign key all need them. A caller can
        /// therefore also send a plain <c>SELECT</c> here, which gains it nothing: the read floor
        /// guarantees that a <c>danger-raw-write</c> deployment also has <c>read</c>.
        /// </para>
        /// </remarks>
        private static int DmlPolicy(object? userData, int actionCode, utf8z arg1, utf8z arg2, utf8z dbName, utf8z trigger)
        {
            try
            {
                if (actionCode == raw.SQLITE_INSERT || actionCode == raw.SQLITE_UPDATE || actionCode == raw.SQLITE_DELETE)
                {
                    // For these three actions the first argument is the table name.
                    var table = arg1.utf8_to_string();
                    return table is not null && IsInternalObject(table) ? raw.SQLITE_DENY : raw.SQLITE_OK;
                }

                if (actionCode == raw.SQLITE_SELECT || actionCode == raw.SQLITE_READ || actionCode == raw.SQLITE_RECURSIVE)
                {
                    return raw.SQLITE_OK;
                }

                if (actionCode == raw.SQLITE_FUNCTION)
                {
                    var name = arg2.utf8_to_string();
                    return name is not null && !IsDeniedFunction(name) ? raw.SQLITE_OK : raw.SQLITE_DENY;
                }

                return raw.SQLITE_DENY;
            }
            catch
            {
                // An exception must never cross a native callback boundary. Reject instead.
                return raw.SQLITE_DENY;
            }
        }

        private static bool IsInternalObject(string name) =>
            name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase);

        private static bool IsDeniedFunction(string name)
        {
            foreach (var denied in DeniedFunctions)
            {
                if (string.Equals(name, denied, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Stops SQLite on cancellation, and removes both stop paths on dispose.</summary>
    private sealed class InterruptScope : IDisposable
    {
        private readonly sqlite3 _handle;
        private readonly delegate_progress _progress;
        private readonly CancellationTokenRegistration _registration;
        private readonly CancellationToken _cancellationToken;
        private bool _removed;

        public InterruptScope(sqlite3 handle, CancellationToken cancellationToken)
        {
            _handle = handle;
            _cancellationToken = cancellationToken;
            _progress = OnProgress;

            raw.sqlite3_progress_handler(_handle, ProgressHandlerInterval, _progress, null);
            _registration = cancellationToken.Register(static state => raw.sqlite3_interrupt((sqlite3)state!), handle);
        }

        public void Dispose()
        {
            if (_removed)
            {
                return;
            }

            _removed = true;
            _registration.Dispose();
            raw.sqlite3_progress_handler(_handle, 0, (delegate_progress?)null, null);
            GC.KeepAlive(_progress);
        }

        /// <summary>A non-zero return interrupts the running statement.</summary>
        private int OnProgress(object? userData)
        {
            try
            {
                return _cancellationToken.IsCancellationRequested ? 1 : 0;
            }
            catch
            {
                return 1;
            }
        }
    }
}
