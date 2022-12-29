using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lamar;
using Microsoft.Extensions.Logging;
using Weasel.Core;
using Weasel.Core.Migrations;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;
using Wolverine.Transports.Local;

namespace Wolverine.RDBMS;

public abstract partial class MessageDatabase<T> : DatabaseBase<T>,
    IDatabaseBackedMessageStore, IMessageStoreAdmin where T : DbConnection, new()
{
    protected readonly CancellationToken _cancellation;
    private readonly string _findAtLargeIncomingEnvelopeCountsSql;
    private readonly string _outgoingEnvelopeSql;

    protected MessageDatabase(DatabaseSettings databaseSettings, NodeSettings settings,
        ILogger logger) : base(new MigrationLogger(logger), AutoCreate.CreateOrUpdate, databaseSettings.Migrator,
        "WolverineEnvelopeStorage", databaseSettings.ConnectionString!)
    {
        DatabaseSettings = databaseSettings;

        Settings = settings;
        _cancellation = settings.Cancellation;

        var transaction = new DurableStorageSession(databaseSettings, settings.Cancellation);

        Session = transaction;

        _cancellation = settings.Cancellation;
        _deleteIncomingEnvelopeById =
            $"update {DatabaseSettings.SchemaName}.{DatabaseConstants.IncomingTable} set {DatabaseConstants.Status} = '{EnvelopeStatus.Handled}', {DatabaseConstants.KeepUntil} = @keepUntil where id = @id";
        _incrementIncominEnvelopeAttempts =
            $"update {DatabaseSettings.SchemaName}.{DatabaseConstants.IncomingTable} set attempts = @attempts where id = @id";

        // ReSharper disable once VirtualMemberCallInConstructor
        _outgoingEnvelopeSql = determineOutgoingEnvelopeSql(databaseSettings, settings);

        _findAtLargeIncomingEnvelopeCountsSql =
            $"select {DatabaseConstants.ReceivedAt}, count(*) from {DatabaseSettings.SchemaName}.{DatabaseConstants.IncomingTable} where {DatabaseConstants.Status} = '{EnvelopeStatus.Incoming}' and {DatabaseConstants.OwnerId} = {TransportConstants.AnyNode} group by {DatabaseConstants.ReceivedAt}";
    }

    public NodeSettings Settings { get; }

    public DatabaseSettings DatabaseSettings { get; }

    public IMessageStoreAdmin Admin => this;

    public IDurableStorageSession Session { get; }

    public abstract void Describe(TextWriter writer);

    public Task ReassignDormantNodeToAnyNodeAsync(int nodeId)
    {
        var sql = $@"
update {DatabaseSettings.SchemaName}.{DatabaseConstants.IncomingTable}
  set owner_id = 0
where
  owner_id = @owner;

update {DatabaseSettings.SchemaName}.{DatabaseConstants.OutgoingTable}
  set owner_id = 0
where
  owner_id = @owner;
";

        return Session.CreateCommand(sql)
            .With("owner", nodeId)
            .ExecuteNonQueryAsync(_cancellation);
    }

    public async Task ReleaseIncomingAsync(int ownerId)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(_cancellation);

        await conn
            .CreateCommand(
                $"update {DatabaseSettings.SchemaName}.{DatabaseConstants.IncomingTable} set owner_id = 0 where owner_id = @owner")
            .With("owner", ownerId)
            .ExecuteNonQueryAsync(_cancellation);
    }

    public async Task ReleaseIncomingAsync(int ownerId, Uri receivedAt)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(_cancellation);

        var impacted = await conn
            .CreateCommand(
                $"update {DatabaseSettings.SchemaName}.{DatabaseConstants.IncomingTable} set owner_id = 0 where owner_id = @owner and {DatabaseConstants.ReceivedAt} = @uri")
            .With("owner", ownerId)
            .With("uri", receivedAt.ToString())
            .ExecuteNonQueryAsync(_cancellation);
    }

    public Task<IReadOnlyList<IncomingCount>> LoadAtLargeIncomingCountsAsync()
    {
        return Session.CreateCommand(_findAtLargeIncomingEnvelopeCountsSql).FetchList(async reader =>
        {
            var address = new Uri(await reader.GetFieldValueAsync<string>(0, _cancellation).ConfigureAwait(false));
            var count = await reader.GetFieldValueAsync<int>(1, _cancellation).ConfigureAwait(false);

            return new IncomingCount(address, count);
        }, _cancellation);
    }

    public IDurabilityAgent BuildDurabilityAgent(IWolverineRuntime runtime, IContainer container)
    {
        var durabilityLogger = container.GetInstance<ILogger<DurabilityAgent>>();

        // TODO -- use the worker queue for Retries?
        var worker = new DurableReceiver(new LocalQueue("scheduled"), runtime, runtime.Pipeline);
        return new DurabilityAgent(runtime, runtime.Logger, durabilityLogger, worker, runtime.Storage,
            runtime.Options.Node, DatabaseSettings);
    }

    public void Dispose()
    {
        Session?.Dispose();
    }
}