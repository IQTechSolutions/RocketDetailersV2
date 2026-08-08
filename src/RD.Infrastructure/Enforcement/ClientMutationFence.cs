using System.Data;
using System.Data.Common;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace RD.Infrastructure.Enforcement;

/// <summary>
/// A SQL Server session application lock that serializes enforcement/billing
/// writes with enforcement-relevant mapping changes for one client. Callers
/// must hold the returned handle through their final vendor write or mapping
/// commit. Session ownership lets the fence span several SaveChanges calls and
/// external read-backs without forcing one long database transaction.
/// </summary>
public sealed class ClientMutationFence : IAsyncDisposable
{
    private const int LockTimeoutMilliseconds = 30_000;
    private readonly RdDbContext _db;
    private readonly DbConnection _connection;
    private readonly string _resource;
    private readonly bool _openedHere;
    private bool _released;

    private ClientMutationFence(
        RdDbContext db,
        DbConnection connection,
        string resource,
        bool openedHere)
    {
        _db = db;
        _connection = connection;
        _resource = resource;
        _openedHere = openedHere;
    }

    public static async Task<ClientMutationFence> AcquireAsync(
        RdDbContext db,
        Guid clientId,
        CancellationToken ct = default)
        => await AcquireResourceAsync(
            db,
            $"rd:client-mutation:{clientId:N}",
            $"client {clientId}",
            ct);

    /// <summary>
    /// Serializes the final external-id ownership read/write with vendor
    /// attribution. Callers that also need client fences acquire those first,
    /// then this global ownership fence, so the lock order stays deterministic.
    /// Vendor ingestion acquires only this fence after completing slow API reads.
    /// </summary>
    public static async Task<ClientMutationFence> AcquireMappingOwnershipAsync(
        RdDbContext db,
        CancellationToken ct = default)
        => await AcquireResourceAsync(
            db,
            "rd:mapping-ownership",
            "external identity ownership",
            ct);

    private static async Task<ClientMutationFence> AcquireResourceAsync(
        RdDbContext db,
        string resource,
        string description,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
            await db.Database.OpenConnectionAsync(ct);

        try
        {
            var result = await ExecuteLockCommandAsync(
                db,
                connection,
                "sys.sp_getapplock",
                resource,
                includeTimeout: true,
                ct);
            if (result < 0)
                throw new TimeoutException(
                    $"Timed out waiting for another operation on {description} (SQL application lock result {result}).");

            return new ClientMutationFence(db, connection, resource, openedHere);
        }
        catch
        {
            if (openedHere)
                await db.Database.CloseConnectionAsync();
            throw;
        }
    }

    /// <summary>
    /// Acquires several client fences on the same SQL session in a stable order.
    /// Bulk operations use this so they cannot deadlock one another or cross a
    /// single-client billing, mapping, merge, or enforcement mutation.
    /// </summary>
    public static async Task<IAsyncDisposable> AcquireManyAsync(
        RdDbContext db,
        IEnumerable<Guid> clientIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clientIds);

        var acquired = new List<ClientMutationFence>();
        try
        {
            foreach (var clientId in clientIds.Distinct().OrderBy(id => id))
                acquired.Add(await AcquireAsync(db, clientId, ct));

            return new FenceSet(acquired);
        }
        catch
        {
            for (var index = acquired.Count - 1; index >= 0; index--)
                await acquired[index].DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_released) return;
        _released = true;

        var releaseFailed = false;
        try
        {
            if (_connection.State == ConnectionState.Open)
            {
                var result = await ExecuteLockCommandAsync(
                    _db,
                    _connection,
                    "sys.sp_releaseapplock",
                    _resource,
                    includeTimeout: false,
                    CancellationToken.None);
                if (result < 0)
                {
                    releaseFailed = true;
                    Trace.TraceError(
                        "SQL Server could not release client mutation fence '{0}' (result {1}). The connection will be discarded.",
                        _resource,
                        result);
                }
            }
            else
            {
                releaseFailed = true;
                Trace.TraceError(
                    "The SQL connection closed before client mutation fence '{0}' could be released. Its pool will be cleared.",
                    _resource);
            }
        }
        catch (Exception ex)
        {
            // Never let cleanup failure re-enter a vendor-write retry path after
            // the protected operation has already succeeded. Discarding the
            // physical SQL session releases any surviving session-owned app lock.
            releaseFailed = true;
            Trace.TraceError(
                "Failed to release client mutation fence '{0}'. The connection will be discarded. {1}",
                _resource,
                ex);
        }
        finally
        {
            try
            {
                if (releaseFailed && _connection is SqlConnection sqlConnection)
                    SqlConnection.ClearPool(sqlConnection);
                if (_openedHere || releaseFailed)
                    await _db.Database.CloseConnectionAsync();
            }
            catch (Exception ex)
            {
                // Cleanup must remain non-throwing for the same exactly-once
                // reason. A broken connection is unusable and will be discarded
                // by the provider when the context is disposed.
                Trace.TraceError(
                    "Failed to close the SQL connection after releasing client mutation fence '{0}'. {1}",
                    _resource,
                    ex);
            }
        }
    }

    private static async Task<int> ExecuteLockCommandAsync(
        RdDbContext db,
        DbConnection connection,
        string procedure,
        string resource,
        bool includeTimeout,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        var currentTransaction = db.Database.CurrentTransaction;
        if (currentTransaction is not null)
            command.Transaction = currentTransaction.GetDbTransaction();

        command.CommandText = includeTimeout
            ? $"DECLARE @result int; EXEC @result = {procedure} @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = @timeout; SELECT @result;"
            : $"DECLARE @result int; EXEC @result = {procedure} @Resource = @resource, @LockOwner = 'Session'; SELECT @result;";

        var resourceParameter = command.CreateParameter();
        resourceParameter.ParameterName = "@resource";
        resourceParameter.Value = resource;
        command.Parameters.Add(resourceParameter);

        if (includeTimeout)
        {
            var timeoutParameter = command.CreateParameter();
            timeoutParameter.ParameterName = "@timeout";
            timeoutParameter.Value = LockTimeoutMilliseconds;
            command.Parameters.Add(timeoutParameter);
        }

        var raw = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt32(raw, System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class FenceSet(IReadOnlyList<ClientMutationFence> fences) : IAsyncDisposable
    {
        private bool _released;

        public async ValueTask DisposeAsync()
        {
            if (_released) return;
            _released = true;

            for (var index = fences.Count - 1; index >= 0; index--)
                await fences[index].DisposeAsync();
        }
    }
}
