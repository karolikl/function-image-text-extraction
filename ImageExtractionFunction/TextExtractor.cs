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
using System.Linq;
using System.Net.Http;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http.Json;
using System.Collections.Generic;
using System.Net.Http.Headers;

namespace ImageFunctions
{
    public static class TextExtractor
    {
        private static readonly string BLOB_STORAGE_CONNECTION_STRING = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
        private static readonly string VISION_KEY = Environment.GetEnvironmentVariable("VISION_KEY");
        private static readonly string VISION_ENDPOINT = Environment.GetEnvironmentVariable("VISION_ENDPOINT");
        private static readonly string EXTRACTED_TEXT_CONTAINER_NAME = Environment.GetEnvironmentVariable("EXTRACTEDTEXT_CONTAINER_NAME");
        private static readonly string OPENAI_KEY = Environment.GetEnvironmentVariable("OPENAI_KEY");
        private static readonly string OPENAI_ENDPOINT = Environment.GetEnvironmentVariable("OPENAI_ENDPOINT");
        private static readonly string TRANSLATEDTEXT_CONTAINER_NAME = Environment.GetEnvironmentVariable("TRANSLATEDTEXT_CONTAINER_NAME");

        private static string GetBlobNameFromUrl(string bloblUrl, ILogger log)
        {
            if (string.IsNullOrEmpty(bloblUrl))
            {
                log.LogError("Parameter bloblUrl in GetBlobNameFromUrl cannot be empty");
                return null;
            }

            var uri = new Uri(bloblUrl);
            var blobClient = new BlobClient(uri);
            string filenameWithoutExtension = Path.GetFileNameWithoutExtension(blobClient.Name);
            return filenameWithoutExtension;
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

                var createdEvent = JsonConvert.DeserializeObject<StorageBlobCreatedEventData>(eventGridEvent.Data.ToString());

                dynamic jsonObject = JsonConvert.DeserializeObject(eventGridEvent.Data.ToString());
                string imageUrl = jsonObject.url;
                log.LogInformation($"Received Blob: {imageUrl}");

                var blobName = GetBlobNameFromUrl(imageUrl, log);
                log.LogInformation($"Extracted blob name from url: {blobName}");

                ComputerVisionClient visionClient = AuthenticateVision(VISION_ENDPOINT, VISION_KEY);
                if (visionClient == null)
                    log.LogError("Could not initialize ComputerVisionClient, authentication failed");
                else
                    log.LogInformation($"ComputerVisionClient initialized");

                var textHeaders = await visionClient.ReadAsync(imageUrl, "fr");  // Read text from URL
                log.LogInformation($"Sleeping for 2 seconds");
                Thread.Sleep(2000);
                string operationLocation = textHeaders.OperationLocation; // After the request, get the operation location (operation ID)
                log.LogInformation($"OperationLocation: {operationLocation}");

                // Retrieve the URI where the extracted text will be stored from the Operation-Location header.
                // We only need the ID and not the full URL
                const int numberOfCharsInOperationId = 36;
                string operationId = operationLocation.Substring(operationLocation.Length - numberOfCharsInOperationId);
                log.LogInformation($"OperationId: {operationId}");

                // Extract the text
                ReadOperationResult results;
                log.LogInformation($"Beginning extraction of text from file {blobName}...");

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
                    var extractedBlobName = string.Concat(blobName, ".json");
                    await blobContainerClient.UploadBlobAsync(extractedBlobName, output);
                }

                // CALL LLM

                using (HttpClient httpClient = new HttpClient())
                {
                    // Set the authorization header
                    httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", OPENAI_KEY);

                    var requestData = new
                    {
                        model = "gpt-3.5-turbo", // The chat model
                        messages = new[] {
                        new
                        {
                            role = "system",
                            content = "You are a helpful assistant."
                        },
                        new
                        {
                            role = "user",
                            content = $"In the french text below, fix all spelling mistakes and fill in any missing words. Output the results in french and english. Text: {stringBuilder.ToString()}"
                        }
                    }
                    };

                    // Serialize the request data to JSON
                    string requestDataJson = JsonConvert.SerializeObject(requestData);

                    // Create the HTTP request content
                    var content = new StringContent(requestDataJson, Encoding.UTF8, "application/json");

                    // Send the POST request to the OpenAI API
                    log.LogInformation($"Calling OpenAI.");
                    var response = await httpClient.PostAsync(OPENAI_ENDPOINT, content);
                    log.LogInformation($"Sleeping for 10 seconds");
                    Thread.Sleep(10000);

                    if (response.IsSuccessStatusCode)
                    {
                        log.LogInformation($"OpenAI response successful");
                        string responseJson = await response.Content.ReadAsStringAsync();
                        log.LogInformation($"OpenAI response: {responseJson}");
                        var finalBlobContainerClient = blobServiceClient.GetBlobContainerClient(TRANSLATEDTEXT_CONTAINER_NAME);
                        using (var output = new MemoryStream())
                        {
                            byte[] bytes = Encoding.UTF8.GetBytes(responseJson);
                            output.Write(bytes, 0, bytes.Length);
                            output.Position = 0;
                            var translatedBlobName = string.Concat(blobName, "_tranlation.json");
                            await finalBlobContainerClient.UploadBlobAsync(translatedBlobName, output);
                        }
                    }
                    else
                    {
                        log.LogError($"Failed to process the text. Status Code: {response.StatusCode}");
                    }
                }
                }
            catch (Exception ex)
            {
                log.LogError(ex.Message);
                throw;
            }
        }
    }
    class ApiResponse
    {
        public List<Choice> choices { get; set; }

        public class Choice
        {
            public string text { get; set; }
        }
    }
}
