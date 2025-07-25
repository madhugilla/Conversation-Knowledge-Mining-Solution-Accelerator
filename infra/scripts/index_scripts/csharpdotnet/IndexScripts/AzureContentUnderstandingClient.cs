using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Microsoft.Extensions.Logging;

namespace IndexScripts;

public class AzureContentUnderstandingClient
{
    private readonly HttpClient _httpClient;
    private readonly string _endpoint;
    private readonly string _apiVersion;
    private readonly ILogger _logger;
    private readonly TokenCredential? _tokenCredential;

    public AzureContentUnderstandingClient(
        string endpoint,
        string apiVersion,
        string? subscriptionKey = null,
        TokenCredential? tokenCredential = null,
        ILogger? logger = null,
        HttpClient? httpClient = null)
    {
        if (string.IsNullOrEmpty(subscriptionKey) && tokenCredential == null)
            throw new ArgumentException("Either subscription key or token credential must be provided.");
        
        if (string.IsNullOrEmpty(apiVersion))
            throw new ArgumentException("API version must be provided.");
        
        if (string.IsNullOrEmpty(endpoint))
            throw new ArgumentException("Endpoint must be provided.");

        _endpoint = endpoint.TrimEnd('/');
        _apiVersion = apiVersion;
        _tokenCredential = tokenCredential;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        _httpClient = httpClient ?? new HttpClient();

        if (!string.IsNullOrEmpty(subscriptionKey))
        {
            _httpClient.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", subscriptionKey);
        }
        
        _httpClient.DefaultRequestHeaders.Add("x-ms-useragent", "cu-sample-code");
    }

    private string GetAnalyzerUrl(string analyzerId)
        => $"{_endpoint}/contentunderstanding/analyzers/{analyzerId}?api-version={_apiVersion}";

    private string GetAnalyzerListUrl()
        => $"{_endpoint}/contentunderstanding/analyzers?api-version={_apiVersion}";

    private string GetAnalyzeUrl(string analyzerId)
        => $"{_endpoint}/contentunderstanding/analyzers/{analyzerId}:analyze?api-version={_apiVersion}";

    private async Task<string> GetAccessTokenAsync()
    {
        if (_tokenCredential == null)
            return string.Empty;

        var tokenRequestContext = new TokenRequestContext(new[] { "https://cognitiveservices.azure.com/.default" });
        var token = await _tokenCredential.GetTokenAsync(tokenRequestContext, CancellationToken.None);
        return token.Token;
    }

    private async Task SetAuthenticationHeaderAsync()
    {
        if (_tokenCredential != null)
        {
            var token = await GetAccessTokenAsync();
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    public async Task<JsonDocument> GetAllAnalyzersAsync()
    {
        await SetAuthenticationHeaderAsync();
        
        var response = await _httpClient.GetAsync(GetAnalyzerListUrl());
        response.EnsureSuccessStatusCode();
        
        var content = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(content);
    }

    public async Task<JsonDocument> GetAnalyzerDetailByIdAsync(string analyzerId)
    {
        await SetAuthenticationHeaderAsync();
        
        var response = await _httpClient.GetAsync(GetAnalyzerUrl(analyzerId));
        response.EnsureSuccessStatusCode();
        
        var content = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(content);
    }

    public async Task<HttpResponseMessage> BeginCreateAnalyzerAsync(
        string analyzerId,
        JsonDocument? analyzerTemplate = null,
        string? analyzerTemplatePath = null,
        string? trainingStorageContainerSasUrl = null,
        string? trainingStorageContainerPathPrefix = null)
    {
        JsonDocument? template = analyzerTemplate;
        
        if (!string.IsNullOrEmpty(analyzerTemplatePath) && File.Exists(analyzerTemplatePath))
        {
            var json = await File.ReadAllTextAsync(analyzerTemplatePath);
            template = JsonDocument.Parse(json);
        }

        if (template == null)
            throw new ArgumentException("Analyzer template must be provided.");

        var templateDict = JsonSerializer.Deserialize<Dictionary<string, object>>(template.RootElement.GetRawText());
        
        if (!string.IsNullOrEmpty(trainingStorageContainerSasUrl) && 
            !string.IsNullOrEmpty(trainingStorageContainerPathPrefix))
        {
            templateDict!["trainingData"] = new Dictionary<string, object>
            {
                ["containerUrl"] = trainingStorageContainerSasUrl,
                ["kind"] = "blob",
                ["prefix"] = trainingStorageContainerPathPrefix
            };
        }

        await SetAuthenticationHeaderAsync();

        var jsonContent = JsonSerializer.Serialize(templateDict);
        var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
        
        var response = await _httpClient.PutAsync(GetAnalyzerUrl(analyzerId), content);
        response.EnsureSuccessStatusCode();
        
        _logger.LogInformation("Analyzer {AnalyzerId} create request accepted.", analyzerId);
        return response;
    }

    public async Task<HttpResponseMessage> DeleteAnalyzerAsync(string analyzerId)
    {
        await SetAuthenticationHeaderAsync();
        
        var response = await _httpClient.DeleteAsync(GetAnalyzerUrl(analyzerId));
        response.EnsureSuccessStatusCode();
        
        _logger.LogInformation("Analyzer {AnalyzerId} deleted.", analyzerId);
        return response;
    }

    public async Task<HttpResponseMessage> BeginAnalyzeAsync(string analyzerId, string? fileLocation = null, byte[]? fileData = null)
    {
        await SetAuthenticationHeaderAsync();

        HttpContent content;
        
        if (fileData != null)
        {
            content = new ByteArrayContent(fileData);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        }
        else if (!string.IsNullOrEmpty(fileLocation))
        {
            if (File.Exists(fileLocation))
            {
                var data = await File.ReadAllBytesAsync(fileLocation);
                content = new ByteArrayContent(data);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            }
            else if (Uri.IsWellFormedUriString(fileLocation, UriKind.Absolute))
            {
                var urlData = new { url = fileLocation };
                var json = JsonSerializer.Serialize(urlData);
                content = new StringContent(json, Encoding.UTF8, "application/json");
            }
            else
            {
                throw new ArgumentException("File location must be a valid path or URL.");
            }
        }
        else
        {
            throw new ArgumentException("Either file location or file data must be provided.");
        }

        var response = await _httpClient.PostAsync(GetAnalyzeUrl(analyzerId), content);
        response.EnsureSuccessStatusCode();
        
        _logger.LogInformation("Analyzing file with analyzer: {AnalyzerId}", analyzerId);
        return response;
    }

    public async Task<byte[]> GetImageFromAnalyzeOperationAsync(HttpResponseMessage analyzeResponse, string imageId)
    {
        if (!analyzeResponse.Headers.TryGetValues("operation-location", out var operationLocationValues))
            throw new InvalidOperationException("Operation location not found in the analyzer response header.");

        var operationLocation = operationLocationValues.First().Split("?api-version")[0];
        var imageRetrievalUrl = $"{operationLocation}/images/{imageId}?api-version={_apiVersion}";

        try
        {
            await SetAuthenticationHeaderAsync();
            var response = await _httpClient.GetAsync(imageRetrievalUrl);
            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentType?.MediaType != "image/jpeg")
                throw new InvalidOperationException("Expected image/jpeg content type.");

            return await response.Content.ReadAsByteArrayAsync();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP request failed while retrieving image.");
            throw;
        }
    }

    public async Task<JsonDocument> PollResultAsync(
        HttpResponseMessage response,
        int timeoutSeconds = 120,
        int pollingIntervalSeconds = 2)
    {
        if (!response.Headers.TryGetValues("operation-location", out var operationLocationValues))
            throw new InvalidOperationException("Operation location not found in response headers.");

        var operationLocation = operationLocationValues.First();
        var startTime = DateTime.UtcNow;

        while (true)
        {
            var elapsedTime = DateTime.UtcNow - startTime;
            if (elapsedTime.TotalSeconds > timeoutSeconds)
                throw new TimeoutException($"Operation timed out after {elapsedTime.TotalSeconds:F2} seconds.");

            await SetAuthenticationHeaderAsync();
            var pollResponse = await _httpClient.GetAsync(operationLocation);
            pollResponse.EnsureSuccessStatusCode();

            var content = await pollResponse.Content.ReadAsStringAsync();
            var result = JsonDocument.Parse(content);
            
            var status = result.RootElement.GetProperty("status").GetString()?.ToLowerInvariant();
            
            if (status == "succeeded")
            {
                _logger.LogInformation("Request result is ready after {ElapsedSeconds:F2} seconds.", elapsedTime.TotalSeconds);
                return result;
            }
            else if (status == "failed")
            {
                _logger.LogError("Request failed. Reason: {Response}", content);
                throw new InvalidOperationException("Request failed.");
            }
            else
            {
                var operationId = operationLocation.Split('/').Last().Split('?').First();
                _logger.LogInformation("Request {OperationId} in progress ...", operationId);
            }

            await Task.Delay(TimeSpan.FromSeconds(pollingIntervalSeconds));
        }
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
    }
}