﻿using System;
using System.Configuration;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using EventStore.ClientAPI;
using EventStore.ClientAPI.Embedded;
using EventStore.ClientAPI.SystemData;
using Newtonsoft.Json;
using NUnit.Framework;
using Paramol.Executors;
using Paramol.SqlClient;
using Projac;
using Recipes.DataDefinition;
using Recipes.Shared;

namespace Recipes.EventStoreIntegration
{
    [TestFixture, Explicit]
    public class Usage
    {
        [Test]
        public async void ShowWithStream()
        {
            //setup a projection schema (one of many ways)
            var projector = new SqlProjector(Instance.Handlers,
                new TransactionalSqlCommandExecutor(
                    new ConnectionStringSettings(
                        "projac",
                        @"Data Source=(localdb)\ProjectsV12;Initial Catalog=ProjacUsage;Integrated Security=SSPI;",
                        "System.Data.SqlClient"),
                    IsolationLevel.ReadCommitted));
            projector.Project(new object[] { new DropSchema(), new CreateSchema() });

            //setup an embedded eventstore
            var node = EmbeddedVNodeBuilder.
                AsSingleNode().
                NoGossipOnPublicInterface().
                NoStatsOnPublicInterface().
                NoAdminOnPublicInterface().
                OnDefaultEndpoints().
                RunInMemory().
                Build();
            node.Start();

            var connection = EmbeddedEventStoreConnection.Create(node);
            await connection.ConnectAsync();

            //setup a sample stream (using some sample events)
            var portfolioId = Guid.NewGuid();
            var events = new object[]
            {
                new PortfolioAdded {Id = portfolioId, Name = "My Portfolio"},
                new PortfolioRenamed {Id = portfolioId, Name = "Your Portfolio"},
                new PortfolioRemoved {Id = portfolioId}
            };
            var stream = string.Format("portfolio-{0}", portfolioId.ToString("N"));
            var credentials = new UserCredentials("admin", "changeit");
            await connection.AppendToStreamAsync(
                stream,
                ExpectedVersion.Any,
                events.Select(@event => new EventData(
                    Guid.NewGuid(),
                    @event.GetType().FullName,
                    true,
                    Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(@event)),
                    new byte[0])).ToArray(),
                credentials);

            //project the sample stream (until end of stream)
            var result =
                await connection.ReadStreamEventsForwardAsync(stream, StreamPosition.Start, 1, false, credentials);
            while (!result.IsEndOfStream)
            {
                projector.Project(result.
                    Events.
                    Select(@event =>
                        JsonConvert.DeserializeObject(
                            Encoding.UTF8.GetString(@event.Event.Data),
                            Type.GetType(@event.Event.EventType, true))));
                result =
                    await connection.ReadStreamEventsForwardAsync(stream, result.NextEventNumber, 1, false, credentials);
            }
            projector.Project(result.
                    Events.
                    Select(@event =>
                        JsonConvert.DeserializeObject(
                            Encoding.UTF8.GetString(@event.Event.Data),
                            Type.GetType(@event.Event.EventType, true))));

            node.Stop();
        }

        [Test]
        public async void ShowWithCatchupSubscription()
        {
            //setup a projection schema (one of many ways)
            var projector = new SqlProjector(Instance.Handlers,
                new TransactionalSqlCommandExecutor(
                    new ConnectionStringSettings(
                        "projac",
                        @"Data Source=(localdb)\ProjectsV12;Initial Catalog=ProjacUsage;Integrated Security=SSPI;",
                        "System.Data.SqlClient"),
                    IsolationLevel.ReadCommitted));
            projector.Project(new object[] { new DropSchema(), new CreateSchema() });

            //setup an embedded eventstore
            var node = EmbeddedVNodeBuilder.
                AsSingleNode().
                NoGossipOnPublicInterface().
                NoStatsOnPublicInterface().
                NoAdminOnPublicInterface().
                OnDefaultEndpoints().
                RunInMemory().
                Build();
            node.Start();

            var connection = EmbeddedEventStoreConnection.Create(node);
            await connection.ConnectAsync();

            //setup a sample stream (using some sample events)
            var portfolioId = Guid.NewGuid();
            var events = new object[]
            {
                new PortfolioAdded {Id = portfolioId, Name = "My Portfolio"},
                new PortfolioRenamed {Id = portfolioId, Name = "Your Portfolio"},
                new PortfolioRemoved {Id = portfolioId}
            };
            var stream = string.Format("portfolio-{0}", portfolioId.ToString("N"));
            var credentials = new UserCredentials("admin", "changeit");
            await connection.AppendToStreamAsync(
                stream,
                ExpectedVersion.Any,
                events.Select(@event => new EventData(
                    Guid.NewGuid(),
                    @event.GetType().FullName,
                    true,
                    Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(@event)),
                    new byte[0])).ToArray(),
                credentials);

            //project the sample stream (until end of stream)
            var subscription = connection.SubscribeToStreamFrom(stream, StreamPosition.Start, false, (_, @event) =>
            {
                projector.Project(
                    JsonConvert.DeserializeObject(
                        Encoding.UTF8.GetString(@event.Event.Data),
                        Type.GetType(@event.Event.EventType, true)));
            }, userCredentials: credentials);
            //should complete within 5 seconds.
            await Task.Delay(TimeSpan.FromSeconds(5));
            subscription.Stop();
            
            node.Stop();
        }

        private static readonly SqlProjection Instance = new SqlProjectionBuilder().
            When<PortfolioAdded>(@event =>
                TSql.NonQueryStatement(
                    "INSERT INTO [Portfolio] ([Id], [Name], [PhotoCount]) VALUES (@P1, @P2, 0)",
                    new {P1 = TSql.UniqueIdentifier(@event.Id), P2 = TSql.NVarChar(@event.Name, 40)}
                    )).
            When<PortfolioRemoved>(@event =>
                TSql.NonQueryStatement(
                    "DELETE FROM [Portfolio] WHERE [Id] = @P1",
                    new {P1 = TSql.UniqueIdentifier(@event.Id)}
                    )).
            When<PortfolioRenamed>(@event =>
                TSql.NonQueryStatement(
                    "UPDATE [Portfolio] SET [Name] = @P2 WHERE [Id] = @P1",
                    new {P1 = TSql.UniqueIdentifier(@event.Id), P2 = TSql.NVarChar(@event.Name, 40)}
                    )).
             When<CreateSchema>(_ =>
                TSql.NonQueryStatement(
                    @"IF NOT EXISTS (SELECT * FROM SYSOBJECTS WHERE NAME='Portfolio' AND XTYPE='U')
                        BEGIN
                            CREATE TABLE [Portfolio] (
                                [Id] UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_Portfolio PRIMARY KEY, 
                                [Name] NVARCHAR(MAX) NOT NULL,
                                [PhotoCount] INT NOT NULL)
                        END")).
            When<DropSchema>(_ =>
                TSql.NonQueryStatement(
                    @"IF EXISTS (SELECT * FROM SYSOBJECTS WHERE NAME='Portfolio' AND XTYPE='U')
                        DROP TABLE [Portfolio]")).
            When<DeleteData>(_ =>
                TSql.NonQueryStatement(
                    @"IF EXISTS (SELECT * FROM SYSOBJECTS WHERE NAME='Portfolio' AND XTYPE='U')
                        DELETE FROM [Portfolio]")).
            Build();
    }
}
