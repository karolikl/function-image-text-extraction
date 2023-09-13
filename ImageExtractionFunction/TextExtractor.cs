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
using Newtonsoft.Json.Linq;
using System;
using System.IO;
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

        private static string GetBlobNameFromUrl(string bloblUrl)
        {
            var uri = new Uri(bloblUrl);
            var blobClient = new BlobClient(uri);
            return blobClient.Name;
        }

        private static async Task ExtractTextFromImage(string bloblUrl,
            ILogger log)
        {
            try
            {
                ComputerVisionClient client = AuthenticateVision(VISION_ENDPOINT, VISION_KEY);
                if (client == null)
                    log.LogError("Could not initialize ComputerVisionClient, authentication failed");

                // Read text from URL
                var textHeaders = await client.ReadAsync(bloblUrl);
                // After the request, get the operation location (operation ID)
                string operationLocation = textHeaders.OperationLocation;
                Thread.Sleep(2000);

                // Retrieve the URI where the extracted text will be stored from the Operation-Location header.
                // We only need the ID and not the full URL
                const int numberOfCharsInOperationId = 36;
                string operationId = operationLocation.Substring(operationLocation.Length - numberOfCharsInOperationId);

                // Extract the text
                ReadOperationResult results;
                log.LogInformation($"Extracting text from URL file {Path.GetFileName(bloblUrl)}...");

                do
                {
                    results = await client.GetReadResultAsync(Guid.Parse(operationId));
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
            }
            catch (Exception ex)
            {
                log.LogInformation(ex.Message);
                throw;
            }
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
            [Blob("{data.url}", FileAccess.Read)] Stream input,
            ILogger log)
        {
            try
            {
                if (input != null)
                {
                    var dataAsJObject = JObject.Parse(eventGridEvent.Data.ToString());
                    var createdEvent = dataAsJObject.ToObject<StorageBlobCreatedEventData>();

                    var imageUrl = createdEvent.Url;
                    var extension = Path.GetExtension(createdEvent.Url);


                    var thumbContainerName = Environment.GetEnvironmentVariable("EXTRACTEDTEXT_CONTAINER_NAME");
                    var blobServiceClient = new BlobServiceClient(BLOB_STORAGE_CONNECTION_STRING);
                    var blobContainerClient = blobServiceClient.GetBlobContainerClient(thumbContainerName);
                    var blobName = GetBlobNameFromUrl(createdEvent.Url);


                    ComputerVisionClient client = AuthenticateVision(VISION_ENDPOINT, VISION_KEY);
                    if (client == null)
                        log.LogError("Could not initialize ComputerVisionClient, authentication failed");

                    // Read text from URL
                    var textHeaders = await client.ReadAsync(imageUrl);
                    // After the request, get the operation location (operation ID)
                    string operationLocation = textHeaders.OperationLocation;
                    Thread.Sleep(2000);

                    // Retrieve the URI where the extracted text will be stored from the Operation-Location header.
                    // We only need the ID and not the full URL
                    const int numberOfCharsInOperationId = 36;
                    string operationId = operationLocation.Substring(operationLocation.Length - numberOfCharsInOperationId);

                    // Extract the text
                    ReadOperationResult results;
                    log.LogInformation($"Extracting text from URL file {Path.GetFileName(imageUrl)}...");

                    do
                    {
                        results = await client.GetReadResultAsync(Guid.Parse(operationId));
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

                    using (var output = new MemoryStream())
                    {
                        byte[] bytes = Encoding.UTF8.GetBytes(stringBuilder.ToString());
                        output.Write(bytes, 0, bytes.Length);
                        output.Position = 0;
                        await blobContainerClient.UploadBlobAsync(blobName, output);
                    }
                }
            }
            catch (Exception ex)
            {
                log.LogInformation(ex.Message);
                throw;
            }
        }
    }
}
