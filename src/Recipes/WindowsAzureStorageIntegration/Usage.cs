﻿using System;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Table;
using NUnit.Framework;
using Projac.WindowsAzure.Storage;
using Recipes.Shared;

namespace Recipes.WindowsAzureStorageIntegration
{
    [TestFixture, Explicit]
    public class Usage
    {
        [Test]
        public async void ShowUsingDevStorage()
        {
            var account = CloudStorageAccount.DevelopmentStorageAccount;
            var client = account.CreateCloudTableClient();
            var portfolioId = Guid.NewGuid();
            await new AsyncCloudTableProjector(Projection.Handlers).
                ProjectAsync(client, new object[]
                {
                    new RebuildProjection(),
                    new PortfolioAdded {Id = portfolioId, Name = "My portfolio"},
                    new PortfolioRenamed {Id = portfolioId, Name = "Your portfolio"},
                    new PortfolioRemoved {Id = portfolioId}
                });
        }

        public static CloudTableProjection Projection = new CloudTableProjectionBuilder().
            When<RebuildProjection>((client, message) =>
            {
                var table = client.GetTableReference("Portfolio");
                return table.CreateIfNotExistsAsync();
            }).
            When<PortfolioAdded>((client, message) =>
            {
                var table = client.GetTableReference("Portfolio");
                return table.ExecuteAsync(
                    TableOperation.Insert(
                    new PortfolioModel(message.Id)
                    {
                        Name = message.Name
                    }));
            }).
            When<PortfolioRemoved>((client, message) =>
            {
                var table = client.GetTableReference("Portfolio");
                return table.ExecuteAsync(
                    TableOperation.Delete(new PortfolioModel(message.Id)
                    {
                        ETag = "*"
                    }));   
            }).
            When<PortfolioRenamed>((client, message) =>
            {
                var table = client.GetTableReference("Portfolio");
                return table.ExecuteAsync(
                    TableOperation.Merge(new PortfolioModel(message.Id)
                    {
                        Name = message.Name,
                        ETag = "*"
                    }));
            }).
            Build();
    }
}
