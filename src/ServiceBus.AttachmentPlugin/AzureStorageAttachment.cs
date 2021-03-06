﻿namespace ServiceBus.AttachmentPlugin
{
    using System;
    using System.Threading.Tasks;
    using Microsoft.Azure.ServiceBus;
    using Microsoft.Azure.ServiceBus.Core;
    using Microsoft.WindowsAzure.Storage;
    using Microsoft.WindowsAzure.Storage.Blob;

    class AzureStorageAttachment : ServiceBusPlugin
    {
        const string NotForUseWarning = "This API exposed for the purposes of plugging into the Azure ServiceBus extensibility pipeline. It is not intended to be consumed by systems using this plugin.";
        const string MessageId = "_MessageId";
        internal const string ValidUntilUtc = "_ValidUntilUtc";
        internal const string DateFormat = "yyyy-MM-dd HH:mm:ss:ffffff Z";

        Lazy<CloudBlobClient> client;
        AzureStorageAttachmentConfiguration configuration;

        public AzureStorageAttachment(AzureStorageAttachmentConfiguration configuration)
        {
            Guard.AgainstNull(nameof(configuration), configuration);
            var account = CloudStorageAccount.Parse(configuration.ConnectionString);
            client = new Lazy<CloudBlobClient>(() => account.CreateCloudBlobClient());
            this.configuration = configuration;
        }

        public override string Name => nameof(AzureStorageAttachment);

        internal static Func<DateTime> DateTimeFunc = () => DateTime.UtcNow;

        public override async Task<Message> BeforeMessageSend(Message message)
        {
            if (!configuration.MessageMaxSizeReachedCriteria(message))
            {
                return message;
            }

            var container = client.Value.GetContainerReference(configuration.ContainerName);
            await container.CreateIfNotExistsAsync().ConfigureAwait(false);
            var blob = container.GetBlockBlobReference(Guid.NewGuid().ToString());
            SetValidMessageId(blob, message.MessageId);
            SetValidUntil(blob, message.TimeToLive);

            await blob.UploadFromByteArrayAsync(message.Body,0, message.Body.Length).ConfigureAwait(false);

            message.Body = null;
            message.UserProperties[configuration.MessagePropertyToIdentifyAttachmentBlob] = blob.Name;
            return message;
        }

        static void SetValidMessageId(ICloudBlob blob, string messageId)
        {
            if (!string.IsNullOrWhiteSpace(messageId))
            {
                blob.Metadata[MessageId] = messageId;
            }
        }

        static void SetValidUntil(ICloudBlob blob, TimeSpan timeToBeReceived)
        {
            if (timeToBeReceived == TimeSpan.MaxValue)
            {
                return;
            }

            var validUntil = DateTimeFunc().Add(timeToBeReceived);
            blob.Metadata[ValidUntilUtc] = validUntil.ToString(DateFormat);
        }

        public override async Task<Message> AfterMessageReceive(Message message)
        {
            var userProperties = message.UserProperties;
            if (!userProperties.ContainsKey(configuration.MessagePropertyToIdentifyAttachmentBlob))
            {
                return message;
            }

            var container = client.Value.GetContainerReference(configuration.ContainerName);
            await container.CreateIfNotExistsAsync().ConfigureAwait(false);
            var blobName = (string)userProperties[configuration.MessagePropertyToIdentifyAttachmentBlob];

            var blob = container.GetBlockBlobReference(blobName);
            await blob.FetchAttributesAsync().ConfigureAwait(false);
            var fileByteLength = blob.Properties.Length;
            var bytes = new byte[fileByteLength];
            await blob.DownloadToByteArrayAsync(bytes, 0).ConfigureAwait(false);
            message.Body = bytes;
            return message;
        }
    }
}
