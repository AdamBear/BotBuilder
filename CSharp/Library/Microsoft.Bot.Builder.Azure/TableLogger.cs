﻿// 
// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license.
// 
// Microsoft Bot Framework: http://botframework.com
// 
// Bot Builder SDK Github:
// https://github.com/Microsoft/BotBuilder
// 
// Copyright (c) Microsoft Corporation
// All rights reserved.
// 
// MIT License:
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED ""AS IS"", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//
using Autofac;
using Microsoft.Bot.Builder.Dialogs;
using Microsoft.Bot.Builder.History;
using Microsoft.Bot.Builder.Internals.Fibers;
using Microsoft.Bot.Connector;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Table;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Bot.Builder.Azure
{
    /// <summary>
    /// Log conversation activities to Azure Table Storage.
    /// </summary>
    /// <remarks>
    /// Activities are limited to ~64k when converted to JSON and compressed.  If an activity is bigger than that,
    /// it will be dropped.  If your activities are larger, you either need to preprocess them first or use another implementation.
    /// </remarks>
    public class TableLogger : IActivityLogger, IActivitySource, IActivityManager
    {
        /// <summary>
        /// Activity entity for table storage.
        /// </summary>
        public class ActivityEntity : TableEntity
        {
            /// <summary>
            /// Empty constructor.
            /// </summary>
            public ActivityEntity()
            { }

            /// <summary>
            /// Construct from an IActivity.
            /// </summary>
            /// <param name="activity"></param>
            public ActivityEntity(IActivity activity)
            {
                PartitionKey = GeneratePartitionKey(activity.ChannelId, activity.Conversation.Id);
                RowKey = GenerateRowKey(activity.Timestamp.Value);
                From = activity.From.Id;
                Recipient = activity.Recipient.Id;
                
                CompressedActivity = JsonConvert.SerializeObject(activity).Compress();
                Version = 3.0;
            }

            /// <summary>
            /// Version number for the underlying activity.
            /// </summary>
            public double Version { get; set; }

            /// <summary>
            /// Channel identifier for sender.
            /// </summary>
            public string From { get; set; }

            /// <summary>
            /// Channel identifier for receiver.
            /// </summary>
            public string Recipient { get; set; }

            /// <summary>
            /// Compressed JSON Serialization of full activity message.
            /// </summary>
            public byte[] CompressedActivity { get; set; }

            /// <summary>
            /// Return acutal IActivity.
            /// </summary>
            /// <returns>Logged IActivity.</returns>
            public IActivity Activity()
            {
                return (IActivity) JsonConvert.DeserializeObject(CompressedActivity.Decompress());
            }

            /// <summary>
            /// Generate activity object from table storage properties.
            /// </summary>
            /// <param name="properties"></param>
            /// <returns>Logged IActivity.</returns>
            public static IActivity Activity(IDictionary<string, EntityProperty> properties)
            {
                return (IActivity)JsonConvert.DeserializeObject(properties["CompressedActivity"].BinaryValue.Decompress());
            }

            /// <summary>
            /// Generate a partition key given <paramref name="channelId"/> and <paramref name="conversationId"/>.
            /// </summary>
            /// <param name="channelId">Channel where activity happened.</param>
            /// <param name="conversationId">Conversation where activity happened.</param>
            /// <returns>Partition key.</returns>
            public static string GeneratePartitionKey(string channelId, string conversationId)
            {
                return $"{channelId}|{conversationId}";
            }

            /// <summary>
            /// Generate row key for ascending <paramref name="timestamp"/>.
            /// </summary>
            /// <param name="timestamp">Timestamp of activity.</param>
            /// <returns></returns>
            public static string GenerateRowKey(DateTime timestamp)
            {
                return $"{timestamp.Ticks:D19}";
            }
        }

        private CloudTable _table = null;

        /// <summary>
        /// Create a table storage logger.
        /// </summary>
        /// <param name="table">Table stroage to use for storing activities.</param>
        public TableLogger(CloudTable table)
        {
            SetField.CheckNull(nameof(_table), table);
            _table = table;
        }

        /// <summary>
        /// Log activity to table storage.
        /// </summary>
        /// <param name="activity">Activity to log.</param>
        Task IActivityLogger.LogAsync(IActivity activity)
        {
            if (!activity.Timestamp.HasValue)
            {
                activity.Timestamp = DateTime.UtcNow;
            }
            return Write(_table, activity);
        }

        IEnumerable<IActivity> IActivitySource.Activities(string channelId, string conversationId, DateTime oldest)
        {
            var query = BuildQuery(channelId, conversationId, oldest);
            return _table.ExecuteQuery(query,
                (partitionKey, rowKey, timestamp, properties, etag) => ActivityEntity.Activity(properties));
        }

        async Task IActivitySource.WalkActivitiesAsync(Func<IActivity, Task> function, string channelId, string conversationId, DateTime oldest, CancellationToken cancel)
        {
            var query = BuildQuery(channelId, conversationId, oldest);
            TableContinuationToken continuationToken = null;
            do
            {
                var results = await _table.ExecuteQuerySegmentedAsync(query, 
                    (paritionKey, rowKy, timestamp, properties, etag) => ActivityEntity.Activity(properties),
                    continuationToken, cancel);
                foreach (var result in results)
                {
                    await function(result);
                }
                continuationToken = results.ContinuationToken;
            } while (continuationToken != null);
        }

#pragma warning disable CS1998,CS4014
        /// <summary>
        /// Delete a specific conversation.
        /// </summary>
        /// <param name="channelId">Channel identifier.</param>
        /// <param name="conversationId">Conversation identifier.</param>
        /// <param name="cancel">Cancellation token.</param>
        /// <returns>Task.</returns>
        async Task IActivityManager.DeleteConversationAsync(string channelId, string conversationId, CancellationToken cancel)
        {
            var pk = ActivityEntity.GeneratePartitionKey(channelId, conversationId);
            var query = new TableQuery<ActivityEntity>()
                .Where(TableQuery.GenerateFilterCondition("PartitionKey", QueryComparisons.Equal, pk))
                .Select(new string[] { "PartitionKey", "RowKey" });
            await DeleteAsync(query, cancel);
        }

        /// <summary>
        /// Delete any conversation records older than <paramref name="oldest"/>.
        /// </summary>
        /// <param name="oldest">Maximum timespan from now to remember.</param>
        /// <param name="cancel">Cancellation token.</param>
        async Task IActivityManager.DeleteBeforeAsync(DateTime oldest, CancellationToken cancel)
        {
            var rowKey = ActivityEntity.GenerateRowKey(oldest);
            var query = new TableQuery<ActivityEntity>()
                .Where(TableQuery.GenerateFilterCondition("RowKey", QueryComparisons.LessThan, rowKey))
                .Select(new string[] { "PartitionKey", "RowKey" });
            await DeleteAsync(query, cancel);
        }

        async Task IActivityManager.DeleteUserActivitiesAsync(string userId, CancellationToken cancel)
        {
            var query = new TableQuery<ActivityEntity>().Select(new string[] { "PartitionKey", "RowKey", "From", "Recipient" });
            await DeleteAsync(query, cancel, qs => (from r in qs where r.From == userId || r.Recipient == userId select r));
        }

        private static TableQuery BuildQuery(string channelId, string conversationId, DateTime oldest)
        {
            var query = new TableQuery();
            string filter = null;
            if (channelId != null && conversationId != null)
            {
                var pkey = ActivityEntity.GeneratePartitionKey(channelId, conversationId);
                filter = TableQuery.GenerateFilterCondition("PartitionKey", QueryComparisons.Equal, pkey);
            }
            else if (channelId != null)
            {
                var pkey = ActivityEntity.GeneratePartitionKey(channelId, "");
                filter = TableQuery.GenerateFilterCondition("ParitionKey", QueryComparisons.GreaterThanOrEqual, pkey);
            }
            if (oldest != default(DateTime))
            {
                var rowKey = ActivityEntity.GenerateRowKey(oldest);
                var rowFilter = TableQuery.GenerateFilterCondition("RowKey", QueryComparisons.GreaterThanOrEqual, rowKey);
                if (filter == null)
                {
                    filter = rowFilter;
                }
                else
                {
                    filter = TableQuery.CombineFilters(filter, TableOperators.And, rowFilter);
                }
            }
            if (filter != null)
            {
                query.Where(filter);
            }
            return query;
        }

#pragma warning restore CS1998,CS4014

        private async Task DeleteAsync(TableQuery<ActivityEntity> query, CancellationToken cancel, Func<IEnumerable<ActivityEntity>, IEnumerable<ActivityEntity>> filter = null)
        {
            TableContinuationToken continuationToken = null;
            do
            {
                var results = await _table.ExecuteQuerySegmentedAsync(query, continuationToken, cancel);
                var partitionKey = string.Empty;
                var batch = new TableBatchOperation();
                foreach (var result in filter == null ? results : filter(results))
                {
                    if (result.PartitionKey == partitionKey)
                    {
                        if (batch.Count == 100)
                        {
                            await _table.ExecuteBatchAsync(batch, cancel);
                            batch = new TableBatchOperation();
                        }
                    }
                    else
                    {
                        if (batch.Count > 0)
                        {
                            await _table.ExecuteBatchAsync(batch, cancel);
                            batch = new TableBatchOperation();
                        }
                        partitionKey = result.PartitionKey;
                    }
                    batch.Add(TableOperation.Delete(result));
                }
                if (batch.Count > 0)
                {
                    await _table.ExecuteBatchAsync(batch, cancel);
                }
                continuationToken = results.ContinuationToken;
            } while (continuationToken != null);
        }

        // Write out activity with auto-incrementing of timestamp for conflicts up to 5 times
        private static Task Write(CloudTable table, IActivity activity, int retriesLeft = 5)
        {
            var insert = TableOperation.Insert(new ActivityEntity(activity));
            return table.ExecuteAsync(insert).ContinueWith(t =>
            {
                if (--retriesLeft > 0 && t.IsFaulted)
                {
                    var response = ((t.Exception.InnerException as StorageException)?.InnerException as System.Net.WebException)?.Response as System.Net.HttpWebResponse;
                    if (response != null && response.StatusCode == System.Net.HttpStatusCode.Conflict)
                    {
                        activity.Timestamp = activity.Timestamp.Value.AddTicks(1);
                        return TableLogger.Write(table, activity, retriesLeft);
                    }
                }
                t.Wait();
                return t;
            }).Unwrap();
        }
    }

    /// <summary>
    /// Module for registering a LoggerTable.
    /// </summary>
    public class TableLoggerModule : Autofac.Module
    {
        private CloudStorageAccount _account;
        private string _tableName;

        /// <summary>
        /// Create a TableLogger for a particular storage account and table name.
        /// </summary>
        /// <param name="account">Azure storage account to use.</param>
        /// <param name="tableName">Where to log activities.</param>
        public TableLoggerModule(CloudStorageAccount account, string tableName)
        {
            _account = account;
            _tableName = tableName;
        }

        /// <summary>
        /// Update builder with registration for TableLogger.
        /// </summary>
        /// <param name="builder">Builder to use for registration.</param>
        protected override void Load(ContainerBuilder builder)
        {
            base.Load(builder);
            builder.RegisterInstance(_account)
                .AsSelf();
            builder.Register(c => c.Resolve<CloudStorageAccount>().CreateCloudTableClient())
                .AsSelf()
                .SingleInstance();
            builder.Register(c =>
            {
                var table = c.Resolve<CloudTableClient>().GetTableReference(_tableName);
                table.CreateIfNotExists();
                return table;
            })
                .AsSelf()
                .SingleInstance();
            builder.RegisterType<TableLogger>()
                .AsImplementedInterfaces()
                .SingleInstance();
        }
    }

    static partial class Extensions
    {
        public static void Forget<T>(this Task<T> task)
        {
        }

        public static byte[] Compress(this string str)
        {
            var bytes = Encoding.UTF8.GetBytes(str);

            using (var msi = new MemoryStream(bytes))
            using (var mso = new MemoryStream())
            {
                using (var gs = new GZipStream(mso, CompressionMode.Compress))
                {
                    msi.CopyTo(gs);
                }
                return mso.ToArray();
            }
        }

        public static string Decompress(this byte[] bytes)
        {
            using (var msi = new MemoryStream(bytes))
            using (var mso = new MemoryStream())
            {
                using (var gs = new GZipStream(msi, CompressionMode.Decompress))
                {
                    gs.CopyTo(mso);
                }
                return Encoding.UTF8.GetString(mso.ToArray());
            }
        }
    }
}
