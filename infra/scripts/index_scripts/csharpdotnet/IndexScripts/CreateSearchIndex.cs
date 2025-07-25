using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Microsoft.Extensions.Logging;

namespace IndexScripts;

public class CreateSearchIndex
{
    private const string KeyVaultName = "kv_to-be-replaced";
    private const string ManagedIdentityClientId = "mici_to-be-replaced";
    private const string IndexName = "call_transcripts_index";

    private readonly ILogger<CreateSearchIndex> _logger;
    private readonly DefaultAzureCredential _credential;

    public CreateSearchIndex(ILogger<CreateSearchIndex> logger)
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

    public async Task CreateSearchIndexAsync()
    {
        _logger.LogInformation("Creating or updating Azure Cognitive Search index...");

        // Retrieve secrets from Key Vault
        var searchEndpoint = await GetSecretsFromKeyVaultAsync("AZURE-SEARCH-ENDPOINT");
        var openaiResourceUrl = await GetSecretsFromKeyVaultAsync("AZURE-OPENAI-ENDPOINT");
        var embeddingModel = await GetSecretsFromKeyVaultAsync("AZURE-OPENAI-EMBEDDING-MODEL");

        var indexClient = new SearchIndexClient(new Uri(searchEndpoint), _credential);

        // Define index schema
        var fields = new List<SearchField>
        {
            new SearchField("id", SearchFieldDataType.String) { IsKey = true },
            new SearchField("chunk_id", SearchFieldDataType.String),
            new SearchField("content", SearchFieldDataType.String),
            new SearchField("sourceurl", SearchFieldDataType.String),
            new SearchField("contentVector", SearchFieldDataType.Collection(SearchFieldDataType.Single))
            {
                VectorSearchDimensions = 1536,
                VectorSearchProfileName = "myHnswProfile"
            }
        };

        // Define vector search settings
        var vectorSearch = new VectorSearch();
        
        // Add HNSW algorithm configuration
        vectorSearch.Algorithms.Add(new HnswAlgorithmConfiguration("myHnsw"));
        
        // Add vector search profile
        vectorSearch.Profiles.Add(new VectorSearchProfile("myHnswProfile", "myHnsw")
        {
            VectorizerName = "myOpenAI"
        });
        
        // Add Azure OpenAI vectorizer
        vectorSearch.Vectorizers.Add(new AzureOpenAIVectorizer("myOpenAI")
        {
            Parameters = new AzureOpenAIVectorizerParameters
            {
                ResourceUri = new Uri(openaiResourceUrl),
                DeploymentName = embeddingModel,
                ModelName = embeddingModel
            }
        });

        // Define semantic configuration
        var semanticConfig = new SemanticConfiguration("my-semantic-config", new SemanticPrioritizedFields
        {
            KeywordsFields = { new SemanticField("chunk_id") },
            ContentFields = { new SemanticField("content") }
        });

        // Create the semantic settings with the configuration
        var semanticSearch = new SemanticSearch();
        semanticSearch.Configurations.Add(semanticConfig);

        // Define and create the index
        var index = new SearchIndex(IndexName, fields)
        {
            VectorSearch = vectorSearch,
            SemanticSearch = semanticSearch
        };

        var result = await indexClient.CreateOrUpdateIndexAsync(index);
        _logger.LogInformation("Search index '{IndexName}' created or updated successfully.", result.Value.Name);
    }
}