using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Management.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent.Authentication;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;

using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Azure.Identity;
using System.Text;

namespace StorageMSIFunction
{
    public static class Functions
    {
        private static readonly Lazy<IDictionary<string, BlobServiceClient>> _serviceClients = new Lazy<IDictionary<string, BlobServiceClient>>(() => new Dictionary<string, BlobServiceClient>());
        private static readonly Lazy<TokenCredential> _msiCredential = new Lazy<TokenCredential>(() =>
        {
            // https://docs.microsoft.com/en-us/dotnet/api/azure.identity.defaultazurecredential?view=azure-dotnet
            // Using DefaultAzureCredential allows for local dev by setting environment variables for the current user, provided said user
            // has the necessary credentials to perform the operations the MSI of the Function app needs in order to do its work. Including
            // interactive credentials will allow browser-based login when developing locally.
            return new Azure.Identity.DefaultAzureCredential(includeInteractiveCredentials: true);
        });

        private static readonly Lazy<IAzure> _legacyAzure = new Lazy<IAzure>(() =>
        {
            // If we find tenant and subscription in environment variables, configure accordingly
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(@"AZURE_TENANT_ID"))
                && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(@"AZURE_SUBSCRIPTION_ID")))
            {
                var tokenCred = _msiCredential.Value;
                var armToken = tokenCred.GetToken(new TokenRequestContext(scopes: new[] { "https://management.azure.com/.default" }, parentRequestId: null), default).Token;
                var armCreds = new Microsoft.Rest.TokenCredentials(armToken);

                var graphToken = tokenCred.GetToken(new TokenRequestContext(scopes: new[] { "https://graph.windows.net/.default" }, parentRequestId: null), default).Token;
                var graphCreds = new Microsoft.Rest.TokenCredentials(graphToken);

                var credentials = new AzureCredentials(armCreds, graphCreds, Environment.GetEnvironmentVariable(@"AZURE_TENANT_ID"), AzureEnvironment.AzureGlobalCloud);

                return Microsoft.Azure.Management.Fluent.Azure
                    .Authenticate(credentials)
                    .WithSubscription(Environment.GetEnvironmentVariable(@"AZURE_SUBSCRIPTION_ID"));
            }
            else
            {
                var credentials = SdkContext.AzureCredentialsFactory
                    .FromSystemAssignedManagedServiceIdentity(MSIResourceType.AppService, AzureEnvironment.AzureGlobalCloud);
                return Microsoft.Azure.Management.Fluent.Azure
                    .Authenticate(credentials)
                    .WithDefaultSubscription();
            }
        });

        [FunctionName(nameof(WriteMessage))]
        public static async Task<IActionResult> WriteMessage(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequest req,
            ILogger log){

            log.LogInformation("C# HTTP trigger function processed a request.");
            string requestBody;
            using (StreamReader streamReader = new StreamReader(req.Body))
            {
                requestBody = await streamReader.ReadToEndAsync();
            }
            dynamic data = JsonConvert.DeserializeObject(requestBody);

            string accountName = data?.accountName;
            string jsonToUpload = data?.message;

            if (String.IsNullOrEmpty(accountName))
            {
                return new BadRequestObjectResult($@"Request must contain json element 'accountName' designating the storage account for which you wish send message");
            }

            if (String.IsNullOrEmpty(jsonToUpload))
            {
                return new BadRequestObjectResult($@"Request must contain json element 'message' which is the message to upload");
            }

            //BlobServiceClient blobServiceClient = new BlobServiceClient(
            //        new Uri($"https://{accountName}.blob.core.windows.net"),
            //        new ManagedIdentityCredential());


            var container = new Uri($"https://{accountName}.blob.core.windows.net/messages");

            BlobContainerClient containerClient = new BlobContainerClient(container, new ManagedIdentityCredential());
            containerClient.CreateIfNotExists();
         
            string fileName = "message-" + Guid.NewGuid().ToString() + ".txt";
            BlobClient blobClient = containerClient.GetBlobClient(fileName);

            using (MemoryStream ms = new MemoryStream(Encoding.UTF8.GetBytes(jsonToUpload)))
            {
                await blobClient.UploadAsync(ms);
            }


            return (ActionResult)new OkObjectResult($"written to , {accountName}");

        }

        [FunctionName(nameof(GetSASUrl))]
        public static IActionResult GetSASUrl(
            [HttpTrigger(AuthorizationLevel.Function, "get", Route = null)] HttpRequest req,
            ILogger log)
        {
            var queryParams = req.GetQueryParameterDictionary();
            if (!queryParams.TryGetValue(@"blobUri", out string blobUriString)
                || string.IsNullOrWhiteSpace(blobUriString))
            {
                return new BadRequestObjectResult($@"Request must contain query parameter 'blobUri' designating the full URI of the Azure blob for which you wish to retrieve a read-only SAS URL");
            }

            var blobUri = new Uri(blobUriString);
            try
            {
                var blobUriBuilder = new BlobUriBuilder(blobUri);

                if (!_serviceClients.Value.TryGetValue(blobUriBuilder.AccountName, out var serviceClient))
                {
                    serviceClient = new BlobServiceClient(new Uri($@"https://{blobUriBuilder.AccountName}.blob.core.windows.net"), _msiCredential.Value);
                    _serviceClients.Value.Add(blobUriBuilder.AccountName, serviceClient);
                }

                // Create a SAS token that's valid for secToLive, with a 30-second backoff for clock skew.
                BlobSasBuilder sasBuilder = new BlobSasBuilder
                {
                    BlobContainerName = blobUriBuilder.BlobContainerName,
                    BlobName = blobUriBuilder.BlobName,
                    Resource = "b", // "b" is for blob
                    StartsOn = DateTimeOffset.UtcNow.AddSeconds(-30),
                    ExpiresOn = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1)
                };

                // Specify read permissions for the SAS.
                sasBuilder.SetPermissions(BlobSasPermissions.Read);

                var userDelegation = serviceClient.GetUserDelegationKey(sasBuilder.StartsOn, sasBuilder.ExpiresOn)?.Value;

                if (userDelegation == null)
                {
                    log.LogError($@"Unable to get a user delegation key from the Storage service for blob {blobUri}");

                    return new ObjectResult($@"Unable to get a user delegation key from the Storage service for blob {blobUri}")
                    {
                        StatusCode = (int)HttpStatusCode.BadGateway
                    };
                }

                var sasToken = sasBuilder.ToSasQueryParameters(userDelegation, blobUriBuilder.AccountName);
                blobUriBuilder.Sas = sasToken;

                // Construct the full URI, including the SAS token.
                return new OkObjectResult(blobUriBuilder.ToUri().ToString());
            }
            catch (Exception e)
            {
                log.LogError(e, $@"Failure retrieving SAS URL for '{blobUri}'");
                return new ObjectResult(e)
                {
                    StatusCode = (int)HttpStatusCode.BadGateway
                };
            }
        }

        [FunctionName(nameof(GetAccountKeys))]
        public static IActionResult GetAccountKeys(
            [HttpTrigger(AuthorizationLevel.Function, "get", Route = null)] HttpRequest req,
            ILogger log)
        {
            if (!req.GetQueryParameterDictionary().TryGetValue(@"accountName", out var accountName))
            {
                return new BadRequestObjectResult($@"Request must contain query parameter 'accountName' designating the storage account for which you wish to retrieve the account keys");
            }

            try
            {
                var storageAccounts = _legacyAzure.Value.StorageAccounts.List();
                var accountKeys = storageAccounts
                    .FirstOrDefault(sa => sa.Name.Equals(accountName, StringComparison.OrdinalIgnoreCase))?
                    .GetKeys();

                log.LogInformation($@"Successfully retrieved keys for '{accountName}'");
                return new OkObjectResult(accountKeys);
            }
            catch (Exception e)
            {
                log.LogError(e, $@"Failure retrieving keys for '{accountName}'");
                return new ObjectResult(e)
                {
                    StatusCode = (int)HttpStatusCode.BadGateway
                };
            }
        }

        [FunctionName(nameof(RegenerateKey))]
        public static IActionResult RegenerateKey(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequest req,
            ILogger log)
        {
            var queryParams = req.GetQueryParameterDictionary();
            if (!queryParams.TryGetValue(@"accountName", out var accountName))
            {
                return new BadRequestObjectResult($@"Request must contain query parameter 'accountName' designating the storage account for which you wish to regenerate a key");
            }

            if (!queryParams.TryGetValue(@"keyName", out var keyName))
            {
                return new BadRequestObjectResult($@"Request must contain query parameter 'keyName' designating the name of the key you wish to regenerate");
            }

            try
            {
                var storageAccounts = _legacyAzure.Value.StorageAccounts.List();
                var newKey = storageAccounts
                    .FirstOrDefault(sa => sa.Name.Equals(accountName, StringComparison.OrdinalIgnoreCase))?
                    .RegenerateKey(keyName)
                    .First();

                log.LogInformation($@"Successfully regenerated key for {accountName}/{newKey.KeyName}. New key value: {newKey.Value}");

                return new OkResult();
            }
            catch (Exception e)
            {
                log.LogError(e, $@"Failure retrieving keys for '{accountName}'");
                return new ObjectResult(e)
                {
                    StatusCode = (int)HttpStatusCode.BadGateway
                };
            }
        }
    }
}
