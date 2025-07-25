using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Logging;

namespace IndexScripts;

public class CreateCuTemplateAudio
{
    private const string KeyVaultName = "kv_to-be-replaced";
    private const string ManagedIdentityClientId = "mici_to-be-replaced";
    private const string AzureAiApiVersion = "2024-12-01-preview";
    private const string AnalyzerId = "ckm-audio";
    private const string AnalyzerTemplateFile = "ckm-analyzer_config_audio.json";

    private readonly ILogger<CreateCuTemplateAudio> _logger;
    private readonly DefaultAzureCredential _credential;

    public CreateCuTemplateAudio(ILogger<CreateCuTemplateAudio> logger)
    {
        _logger = logger;
        _credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            ManagedIdentityClientId = ManagedIdentityClientId
        });
    }

    private async Task<string> GetSecretsFromKeyVaultAsync(string secretName)
    {
        var secretClient = new SecretClient(
            new Uri($"https://{KeyVaultName}.vault.azure.net/"),
            _credential
        );
        
        var secret = await secretClient.GetSecretAsync(secretName);
        return secret.Value.Value;
    }

    public async Task CreateAnalyzerAsync()
    {
        _logger.LogInformation("Creating Content Understanding analyzer for audio...");

        // Fetch endpoint from Key Vault
        var endpoint = await GetSecretsFromKeyVaultAsync("AZURE-OPENAI-CU-ENDPOINT");

        // Initialize Content Understanding Client
        var client = new AzureContentUnderstandingClient(
            endpoint: endpoint,
            apiVersion: AzureAiApiVersion,
            tokenCredential: _credential,
            logger: _logger
        );

        // Create Analyzer
        var response = await client.BeginCreateAnalyzerAsync(AnalyzerId, analyzerTemplatePath: AnalyzerTemplateFile);
        var result = await client.PollResultAsync(response);
        
        _logger.LogInformation("Audio analyzer created successfully.");
        result.Dispose();
    }
}