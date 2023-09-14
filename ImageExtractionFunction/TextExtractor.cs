// Default URL for triggering event grid function in the local environment.
// http://localhost:7071/runtime/webhooks/EventGrid?functionName={functionname}

// Learn how to locally debug an Event Grid-triggered function:
//    https://aka.ms/AA30pjh

// Use for local testing:
//   https://{ID}.ngrok.io/runtime/webhooks/EventGrid?functionName=Thumbnail

using Azure.Messaging.EventGrid;
using Azure.Messaging.EventGrid.SystemEvents;
using Azure.Storage.Blobs;
using Microsoft.Azure.CognitiveServices.Vision.ComputerVision;
using Microsoft.Azure.CognitiveServices.Vision.ComputerVision.Models;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.EventGrid;
using Microsoft.Extensions.Logging;
using Microsoft.VisualBasic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ImageFunctions
{
    public static class TextExtractor
    {
        private static readonly string BLOB_STORAGE_CONNECTION_STRING = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
        private static readonly string VISION_KEY = Environment.GetEnvironmentVariable("VISION_KEY");
        private static readonly string VISION_ENDPOINT = Environment.GetEnvironmentVariable("VISION_ENDPOINT");
        private static readonly string EXTRACTED_TEXT_CONTAINER_NAME = Environment.GetEnvironmentVariable("EXTRACTEDTEXT_CONTAINER_NAME");

        private static string GetBlobNameFromUrl(string bloblUrl)
        {
            var uri = new Uri(bloblUrl);
            var blobClient = new BlobClient(uri);
            return blobClient.Name;
        }
        public static ComputerVisionClient AuthenticateVision(string endpoint, string key)
        {
            ComputerVisionClient client =
              new ComputerVisionClient(new ApiKeyServiceClientCredentials(key))
              { Endpoint = endpoint };
            return client;
        }

        [FunctionName("TextExtractor")]
        public static async Task Run(
        [EventGridTrigger] EventGridEvent eventGridEvent,
        [Blob("{data.url}", FileAccess.Read)] Stream blobStream,
        ILogger log)
        {
            try
            {
                log.LogInformation($"Received eventGridData: {eventGridEvent.Data}");

                var createdEvent = ((JObject)eventGridEvent.Data.ToString()).ToObject<StorageBlobCreatedEventData>();
                if (createdEvent == null)
                    log.LogError("Could not deserialize eventGridEvent to StorageBlobCreatedEventData");
                log.LogInformation($"createdEvent url: {createdEvent.Url}, blob type: {createdEvent.BlobType}");

                // Deserialize the Event Grid event data
                var eventData = JsonConvert.DeserializeObject<StorageBlobCreatedEventData>(eventGridEvent.Data.ToString());
                if (eventData == null)
                    log.LogError("Could not deserialize eventGridEvent to StorageBlobCreatedEventData");

                var imageUrl = eventData.Url;
                log.LogInformation($"Received Blob: {imageUrl}");

                var blobName = GetBlobNameFromUrl(imageUrl);
                log.LogInformation($"Extracted blob name from url: {blobName}");

                ComputerVisionClient visionClient = AuthenticateVision(VISION_ENDPOINT, VISION_KEY);
                if (visionClient == null)
                    log.LogError("Could not initialize ComputerVisionClient, authentication failed");

                var textHeaders = await visionClient.ReadAsync(imageUrl);  // Read text from URL
                string operationLocation = textHeaders.OperationLocation; // After the request, get the operation location (operation ID)
                Thread.Sleep(2000);

                // Retrieve the URI where the extracted text will be stored from the Operation-Location header.
                // We only need the ID and not the full URL
                const int numberOfCharsInOperationId = 36;
                string operationId = operationLocation.Substring(operationLocation.Length - numberOfCharsInOperationId);

                // Extract the text
                ReadOperationResult results;
                log.LogInformation($"Extracting text from URL file {blobName}...");

                do
                {
                    results = await visionClient.GetReadResultAsync(Guid.Parse(operationId));
                    log.LogInformation($"Waiting to read the results.... {results.Status}.");
                }
                while ((results.Status == OperationStatusCodes.Running ||
                    results.Status == OperationStatusCodes.NotStarted));

                // Display the found text.
                var textUrlFileResults = results.AnalyzeResult.ReadResults;
                var stringBuilder = new StringBuilder("");
                foreach (ReadResult page in textUrlFileResults)
                {
                    foreach (Line line in page.Lines)
                    {
                        stringBuilder.AppendLine(line.Text);
                        log.LogInformation(line.Text);
                    }
                }

                var blobServiceClient = new BlobServiceClient(BLOB_STORAGE_CONNECTION_STRING);
                var blobContainerClient = blobServiceClient.GetBlobContainerClient(EXTRACTED_TEXT_CONTAINER_NAME);
                using (var output = new MemoryStream())
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(stringBuilder.ToString());
                    output.Write(bytes, 0, bytes.Length);
                    output.Position = 0;
                    await blobContainerClient.UploadBlobAsync(blobName, output);
                }
            }
            catch (Exception ex)
            {
                log.LogError(ex.Message);
                throw;
            }
        }
    }
}
