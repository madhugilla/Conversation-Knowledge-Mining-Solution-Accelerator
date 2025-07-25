using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Storage.Files.DataLake;
using Azure.AI.OpenAI;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace IndexScripts;

public class CuProcessDataNewData
{
    private const string KeyVaultName = "kv_to-be-replaced";
    private const string ManagedIdentityClientId = "mici_to-be-replaced";
    private const string FileSystemClientName = "data";
    private const string Directory = "custom_transcripts";
    private const string AudioDirectory = "custom_audiodata";
    private const string IndexName = "call_transcripts_index";

    private readonly ILogger<CuProcessDataNewData> _logger;
    private readonly DefaultAzureCredential _credential;

    public CuProcessDataNewData(ILogger<CuProcessDataNewData> logger)
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

    private static string CleanSpacesWithRegex(string text)
    {
        var cleanedText = Regex.Replace(text, @"\s+", " ");
        cleanedText = Regex.Replace(cleanedText, @"\.{2,}", ".");
        return cleanedText;
    }

    private static List<string> ChunkData(string text, int tokensPerChunk = 1024)
    {
        text = CleanSpacesWithRegex(text);
        var sentences = text.Split(new[] { ". " }, StringSplitOptions.None);
        var chunks = new List<string>();
        var currentChunk = new StringBuilder();
        var currentChunkTokenCount = 0;

        foreach (var sentence in sentences)
        {
            var tokens = sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            
            if (currentChunkTokenCount + tokens.Length <= tokensPerChunk)
            {
                if (currentChunk.Length > 0)
                    currentChunk.Append(". ").Append(sentence);
                else
                    currentChunk.Append(sentence);
                currentChunkTokenCount += tokens.Length;
            }
            else
            {
                if (currentChunk.Length > 0)
                    chunks.Add(currentChunk.ToString());
                currentChunk.Clear();
                currentChunk.Append(sentence);
                currentChunkTokenCount = tokens.Length;
            }
        }

        if (currentChunk.Length > 0)
            chunks.Add(currentChunk.ToString());

        return chunks;
    }

    private async Task<float[]> GetEmbeddingsAsync(string text, AzureOpenAIClient openAiClient)
    {
        const string modelId = "text-embedding-ada-002";
        
        try
        {
            var embeddingClient = openAiClient.GetEmbeddingClient(modelId);
            var response = await embeddingClient.GenerateEmbeddingAsync(text);
            return response.Value.ToFloats().ToArray();
        }
        catch
        {
            await Task.Delay(30000);
            try
            {
                var embeddingClient = openAiClient.GetEmbeddingClient(modelId);
                var response = await embeddingClient.GenerateEmbeddingAsync(text);
                return response.Value.ToFloats().ToArray();
            }
            catch
            {
                return Array.Empty<float>();
            }
        }
    }

    private async Task<List<Dictionary<string, object>>> PrepareSearchDocAsync(
        string content, 
        string documentId, 
        string pathName,
        AzureOpenAIClient openAiClient)
    {
        var chunks = ChunkData(content);
        var docs = new List<Dictionary<string, object>>();

        for (int idx = 0; idx < chunks.Count; idx++)
        {
            var chunkId = $"{documentId}_{(idx + 1):D2}";
            var contentVector = await GetEmbeddingsAsync(chunks[idx], openAiClient);

            docs.Add(new Dictionary<string, object>
            {
                ["id"] = chunkId,
                ["chunk_id"] = chunkId,
                ["content"] = chunks[idx],
                ["sourceurl"] = pathName.Split('/').Last(),
                ["contentVector"] = contentVector
            });
        }

        return docs;
    }

    private async Task CreateSearchIndexAsync(string searchEndpoint, string openaiApiBase, string embeddingModel)
    {
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
        vectorSearch.Algorithms.Add(new HnswAlgorithmConfiguration("myHnsw"));
        vectorSearch.Profiles.Add(new VectorSearchProfile("myHnswProfile", "myHnsw")
        {
            VectorizerName = "myOpenAI"
        });
        vectorSearch.Vectorizers.Add(new AzureOpenAIVectorizer("myOpenAI")
        {
            Parameters = new AzureOpenAIVectorizerParameters
            {
                ResourceUri = new Uri(openaiApiBase),
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

    private static async Task CreateTablesAsync(SqlConnection connection)
    {
        const string dropProcessedDataSql = "DROP TABLE IF EXISTS processed_data";
        const string createProcessedDataSql = @"
            CREATE TABLE processed_data (
                ConversationId varchar(255) NOT NULL PRIMARY KEY,
                EndTime varchar(255),
                StartTime varchar(255),
                Content varchar(max),
                summary varchar(3000),
                satisfied varchar(255),
                sentiment varchar(255),
                topic varchar(255),
                key_phrases nvarchar(max),
                complaint varchar(255), 
                mined_topic varchar(255)
            );";

        const string dropKeyPhrasesSql = "DROP TABLE IF EXISTS processed_data_key_phrases";
        const string createKeyPhrasesSql = @"
            CREATE TABLE processed_data_key_phrases (
                ConversationId varchar(255),
                key_phrase varchar(500), 
                sentiment varchar(255),
                topic varchar(255), 
                StartTime varchar(255)
            );";

        await using var dropProcessedDataCommand = new SqlCommand(dropProcessedDataSql, connection);
        await dropProcessedDataCommand.ExecuteNonQueryAsync();
        
        await using var createProcessedDataCommand = new SqlCommand(createProcessedDataSql, connection);
        await createProcessedDataCommand.ExecuteNonQueryAsync();
        
        await using var dropKeyPhrasesCommand = new SqlCommand(dropKeyPhrasesSql, connection);
        await dropKeyPhrasesCommand.ExecuteNonQueryAsync();
        
        await using var createKeyPhrasesCommand = new SqlCommand(createKeyPhrasesSql, connection);
        await createKeyPhrasesCommand.ExecuteNonQueryAsync();
    }

    private async Task<string> CallGpt4Async(string topicsStr, AzureOpenAIClient openAiClient, string deployment)
    {
        var topicPrompt = $@"
            You are a data analysis assistant specialized in natural language processing and topic modeling. 
            Your task is to analyze the given text corpus and identify distinct topics present within the data.
            {topicsStr}
            1. Identify the key topics in the text using topic modeling techniques. 
            2. Choose the right number of topics based on data. Try to keep it up to 8 topics.
            3. Assign a clear and concise label to each topic based on its content.
            4. Provide a brief description of each topic along with its label.
            5. Add parental controls, billing issues like topics to the list of topics if the data includes calls related to them.
            If the input data is insufficient for reliable topic modeling, indicate that more data is needed rather than making assumptions. 
            Ensure that the topics and labels are accurate, relevant, and easy to understand.
            Return the topics and their labels in JSON format.Always add 'topics' node and 'label', 'description' attributes in json.
            Do not return anything else.
            ";

        var chatClient = openAiClient.GetChatClient(deployment);
        var response = await chatClient.CompleteChatAsync([
            new OpenAI.Chat.SystemChatMessage("You are a helpful assistant."),
            new OpenAI.Chat.UserChatMessage(topicPrompt)
        ]);
        
        var content = response.Value.Content[0].Text;
        return content.Replace("```json", "").Replace("```", "");
    }

    private async Task<string> GetMinedTopicMappingAsync(string inputText, List<string> listOfTopics, AzureOpenAIClient openAiClient, string deployment)
    {
        var topicsStr = string.Join(", ", listOfTopics);
        var prompt = $@"You are a data analysis assistant to help find the closest topic for a given text {inputText} 
                        from a list of topics - {topicsStr}.
                        ALWAYS only return a topic from list - {topicsStr}. Do not add any other text.";

        var chatClient = openAiClient.GetChatClient(deployment);
        var response = await chatClient.CompleteChatAsync([
            new OpenAI.Chat.SystemChatMessage("You are a helpful assistant."),
            new OpenAI.Chat.UserChatMessage(prompt)
        ]);
        
        return response.Value.Content[0].Text;
    }

    private async Task<int> ProcessFilesAsync(
        DataLakeFileSystemClient fileSystemClient,
        string directory,
        string analyzerId,
        SqlConnection connection,
        AzureContentUnderstandingClient cuClient,
        SearchClient searchClient,
        AzureOpenAIClient openAiClient,
        List<string> conversationIds,
        List<Dictionary<string, object>> docs,
        int counter,
        string fileExtension = ".json")
    {
        var paths = fileSystemClient.GetPathsAsync(directory).ToBlockingEnumerable().ToList();
        _logger.LogInformation("Processing {FileType} files from {Directory}", fileExtension, directory);

        foreach (var path in paths)
        {
            var fileClient = fileSystemClient.GetFileClient(path.Name);
            var dataFile = await fileClient.ReadAsync();
            var data = new byte[dataFile.Value.ContentLength];
            await dataFile.Value.Content.ReadAsync(data, 0, data.Length);

            try
            {
                var response = await cuClient.BeginAnalyzeAsync(analyzerId, fileData: data);
                var result = await cuClient.PollResultAsync(response);

                var fileName = path.Name.Split('/').Last().Replace("%3A", "_");
                var startTime = fileName.Replace(fileExtension, "")[^19..];
                const string timestampFormat = "yyyy-MM-dd HH_mm_ss";
                var startTimestamp = DateTime.ParseExact(startTime, timestampFormat, null);
                var conversationId = fileName.Split("convo_", 2)[1].Split('_')[0];
                
                conversationIds.Add(conversationId);

                var fields = result.RootElement.GetProperty("result").GetProperty("contents")[0].GetProperty("fields");
                var duration = int.Parse(fields.GetProperty("Duration").GetProperty("valueString").GetString()!);
                var endTimestamp = startTimestamp.AddSeconds(duration);

                var summary = fields.GetProperty("summary").GetProperty("valueString").GetString()!;
                var satisfied = fields.GetProperty("satisfied").GetProperty("valueString").GetString()!;
                var sentiment = fields.GetProperty("sentiment").GetProperty("valueString").GetString()!;
                var topic = fields.GetProperty("topic").GetProperty("valueString").GetString()!;
                var keyPhrases = fields.GetProperty("keyPhrases").GetProperty("valueString").GetString()!;
                var complaint = fields.GetProperty("complaint").GetProperty("valueString").GetString()!;
                var content = fields.GetProperty("content").GetProperty("valueString").GetString()!;

                const string insertSql = @"
                    INSERT INTO processed_data (ConversationId, EndTime, StartTime, Content, summary, satisfied, sentiment, topic, key_phrases, complaint) 
                    VALUES (@ConversationId, @EndTime, @StartTime, @Content, @summary, @satisfied, @sentiment, @topic, @key_phrases, @complaint)";

                await using var command = new SqlCommand(insertSql, connection);
                command.Parameters.AddWithValue("@ConversationId", conversationId);
                command.Parameters.AddWithValue("@EndTime", endTimestamp.ToString("yyyy-MM-dd HH:mm:ss"));
                command.Parameters.AddWithValue("@StartTime", startTimestamp.ToString("yyyy-MM-dd HH:mm:ss"));
                command.Parameters.AddWithValue("@Content", content);
                command.Parameters.AddWithValue("@summary", summary);
                command.Parameters.AddWithValue("@satisfied", satisfied);
                command.Parameters.AddWithValue("@sentiment", sentiment);
                command.Parameters.AddWithValue("@topic", topic);
                command.Parameters.AddWithValue("@key_phrases", keyPhrases);
                command.Parameters.AddWithValue("@complaint", complaint);
                
                await command.ExecuteNonQueryAsync();

                var searchDocs = await PrepareSearchDocAsync(content, conversationId, path.Name, openAiClient);
                docs.AddRange(searchDocs);
                counter++;

                _logger.LogInformation("Processed file {FileName} successfully.", path.Name);
                result.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error processing file {FileName}: {Message}", path.Name, ex.Message);
            }

            if (docs.Count > 0 && counter % 10 == 0)
            {
                await searchClient.UploadDocumentsAsync(docs);
                docs.Clear();
                _logger.LogInformation("{Counter} files uploaded to Azure Search.", counter);
            }
        }

        if (docs.Count > 0)
        {
            await searchClient.UploadDocumentsAsync(docs);
            _logger.LogInformation("Final batch uploaded to Azure Search - {FileType}.", fileExtension);
        }

        _logger.LogInformation("File processing and DB/Search insertion complete - {FileType}.", fileExtension);
        
        return counter;
    }

    public async Task ProcessDataAsync()
    {
        _logger.LogInformation("Starting enhanced data processing for both text and audio...");

        // Retrieve secrets
        var searchEndpoint = await GetSecretsFromKeyVaultAsync("AZURE-SEARCH-ENDPOINT");
        var openaiApiBase = await GetSecretsFromKeyVaultAsync("AZURE-OPENAI-ENDPOINT");
        var openaiApiVersion = await GetSecretsFromKeyVaultAsync("AZURE-OPENAI-PREVIEW-API-VERSION");
        var deployment = await GetSecretsFromKeyVaultAsync("AZURE-OPENAI-DEPLOYMENT-MODEL");
        var accountName = await GetSecretsFromKeyVaultAsync("ADLS-ACCOUNT-NAME");
        var server = await GetSecretsFromKeyVaultAsync("SQLDB-SERVER");
        var database = await GetSecretsFromKeyVaultAsync("SQLDB-DATABASE");
        var azureAiEndpoint = await GetSecretsFromKeyVaultAsync("AZURE-OPENAI-CU-ENDPOINT");
        var embeddingModel = await GetSecretsFromKeyVaultAsync("AZURE-OPENAI-EMBEDDING-MODEL");

        _logger.LogInformation("Secrets retrieved.");

        // Azure Data Lake setup
        var accountUrl = $"https://{accountName}.dfs.core.windows.net";
        var serviceClient = new DataLakeServiceClient(new Uri(accountUrl), _credential);
        var fileSystemClient = serviceClient.GetFileSystemClient(FileSystemClientName);

        _logger.LogInformation("Azure DataLake setup complete.");

        // Azure Search setup
        var searchClient = new SearchClient(new Uri(searchEndpoint), IndexName, _credential);
        var indexClient = new SearchIndexClient(new Uri(searchEndpoint), _credential);

        _logger.LogInformation("Azure Search setup complete.");

        // Delete and recreate the search index
        try
        {
            await indexClient.DeleteIndexAsync(IndexName);
            _logger.LogInformation("Existing search index deleted.");
        }
        catch (Exception ex)
        {
            _logger.LogInformation("No existing index to delete or error deleting: {Message}", ex.Message);
        }

        await CreateSearchIndexAsync(searchEndpoint, openaiApiBase, embeddingModel);

        // SQL Server setup
        var connectionString = $"Server={server};Database={database};Authentication=Active Directory Default;";
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        _logger.LogInformation("SQL Server connection established.");

        // Content Understanding client
        var cuClient = new AzureContentUnderstandingClient(
            endpoint: azureAiEndpoint,
            apiVersion: "2024-12-01-preview",
            tokenCredential: _credential,
            logger: _logger
        );

        _logger.LogInformation("Content Understanding client initialized.");

        // OpenAI client for embeddings and GPT-4
        var openAiClient = new AzureOpenAIClient(new Uri(openaiApiBase), _credential);

        // Create database tables
        await CreateTablesAsync(connection);
        _logger.LogInformation("Database tables created.");

        // Process files and insert into DB and Search
        var conversationIds = new List<string>();
        var docs = new List<Dictionary<string, object>>();
        var counter = 0;

        // Process text transcripts
        counter = await ProcessFilesAsync(
            fileSystemClient,
            Directory,
            "ckm-json",
            connection,
            cuClient,
            searchClient,
            openAiClient,
            conversationIds,
            docs,
            counter,
            ".json");

        // Process audio files
        counter = await ProcessFilesAsync(
            fileSystemClient,
            AudioDirectory,
            "ckm-audio",
            connection,
            cuClient,
            searchClient,
            openAiClient,
            conversationIds,
            docs,
            counter,
            ".wav");

        // Topic mining and mapping
        await ProcessTopicMiningAsync(connection, openAiClient, deployment, conversationIds);

        _logger.LogInformation("All steps completed. Connection closed.");
    }

    private async Task ProcessTopicMiningAsync(SqlConnection connection, AzureOpenAIClient openAiClient, string deployment, List<string> conversationIds)
    {
        // Get distinct topics
        const string getTopicsSql = "SELECT DISTINCT topic FROM processed_data";
        await using var getTopicsCommand = new SqlCommand(getTopicsSql, connection);
        await using var reader = await getTopicsCommand.ExecuteReaderAsync();
        
        var topics = new List<string>();
        while (await reader.ReadAsync())
        {
            topics.Add(reader.GetString(0));
        }
        await reader.CloseAsync();

        // Create mined topics table
        const string dropMinedTopicsSql = "DROP TABLE IF EXISTS km_mined_topics";
        const string createMinedTopicsSql = @"
            CREATE TABLE km_mined_topics (
                label varchar(255) NOT NULL PRIMARY KEY,
                description varchar(255)
            );";

        await using var dropMinedTopicsCommand = new SqlCommand(dropMinedTopicsSql, connection);
        await dropMinedTopicsCommand.ExecuteNonQueryAsync();
        
        await using var createMinedTopicsCommand = new SqlCommand(createMinedTopicsSql, connection);
        await createMinedTopicsCommand.ExecuteNonQueryAsync();

        var topicsStr = string.Join(", ", topics);
        _logger.LogInformation("Topic mining table prepared.");

        // Call GPT-4 for topic mining
        var gptResponse = await CallGpt4Async(topicsStr, openAiClient, deployment);
        var topicResult = JsonSerializer.Deserialize<Dictionary<string, object>>(gptResponse);
        
        if (topicResult != null && topicResult.ContainsKey("topics"))
        {
            var topicsArray = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(topicResult["topics"].ToString()!);
            
            foreach (var topicObj in topicsArray!)
            {
                const string insertTopicSql = "INSERT INTO km_mined_topics (label, description) VALUES (@label, @description)";
                await using var command = new SqlCommand(insertTopicSql, connection);
                command.Parameters.AddWithValue("@label", topicObj["label"]);
                command.Parameters.AddWithValue("@description", topicObj["description"]);
                await command.ExecuteNonQueryAsync();
            }
        }

        _logger.LogInformation("Topics mined and inserted into km_mined_topics.");

        // Get mined topics list
        const string getMinedTopicsSql = "SELECT label FROM km_mined_topics";
        await using var getMinedTopicsCommand = new SqlCommand(getMinedTopicsSql, connection);
        await using var minedTopicsReader = await getMinedTopicsCommand.ExecuteReaderAsync();
        
        var minedTopicsList = new List<string>();
        while (await minedTopicsReader.ReadAsync())
        {
            minedTopicsList.Add(minedTopicsReader.GetString(0));
        }
        await minedTopicsReader.CloseAsync();

        _logger.LogInformation("Mined topics loaded.");

        // Map processed data to mined topics
        const string getProcessedDataSql = "SELECT * FROM processed_data";
        await using var getProcessedDataCommand = new SqlCommand(getProcessedDataSql, connection);
        await using var processedDataReader = await getProcessedDataCommand.ExecuteReaderAsync();
        
        var processedDataRecords = new List<Dictionary<string, object>>();
        while (await processedDataReader.ReadAsync())
        {
            var record = new Dictionary<string, object>();
            for (int i = 0; i < processedDataReader.FieldCount; i++)
            {
                record[processedDataReader.GetName(i)] = processedDataReader.GetValue(i);
            }
            processedDataRecords.Add(record);
        }
        await processedDataReader.CloseAsync();

        foreach (var record in processedDataRecords.Where(r => conversationIds.Contains(r["ConversationId"].ToString()!)))
        {
            var minedTopicStr = await GetMinedTopicMappingAsync(record["topic"].ToString()!, minedTopicsList, openAiClient, deployment);
            
            const string updateSql = "UPDATE processed_data SET mined_topic = @mined_topic WHERE ConversationId = @conversationId";
            await using var updateCommand = new SqlCommand(updateSql, connection);
            updateCommand.Parameters.AddWithValue("@mined_topic", minedTopicStr);
            updateCommand.Parameters.AddWithValue("@conversationId", record["ConversationId"]);
            await updateCommand.ExecuteNonQueryAsync();
        }

        _logger.LogInformation("Processed data mapped to mined topics.");

        // Continue with remaining processing steps...
        await CreateKmProcessedDataTableAsync(connection);
        await UpdateProcessedDataKeyPhrasesAsync(connection, conversationIds);
        await AdjustDatesToCurrentAsync(connection);
    }

    private async Task CreateKmProcessedDataTableAsync(SqlConnection connection)
    {
        const string dropSql = "DROP TABLE IF EXISTS km_processed_data";
        const string createSql = @"
            CREATE TABLE km_processed_data (
                ConversationId varchar(255) NOT NULL PRIMARY KEY,
                StartTime varchar(255),
                EndTime varchar(255),
                Content varchar(max),
                summary varchar(max),
                satisfied varchar(255),
                sentiment varchar(255),
                keyphrases nvarchar(max),
                complaint varchar(255), 
                topic varchar(255)
            );";

        await using var dropCommand = new SqlCommand(dropSql, connection);
        await dropCommand.ExecuteNonQueryAsync();
        
        await using var createCommand = new SqlCommand(createSql, connection);
        await createCommand.ExecuteNonQueryAsync();

        const string insertSql = @"
            INSERT INTO km_processed_data (ConversationId, StartTime, EndTime, Content, summary, satisfied, sentiment, keyphrases, complaint, topic)
            SELECT ConversationId, StartTime, EndTime, Content, summary, satisfied, sentiment, key_phrases as keyphrases, complaint, mined_topic as topic
            FROM processed_data";

        await using var insertCommand = new SqlCommand(insertSql, connection);
        await insertCommand.ExecuteNonQueryAsync();

        _logger.LogInformation("km_processed_data table updated.");
    }

    private async Task UpdateProcessedDataKeyPhrasesAsync(SqlConnection connection, List<string> conversationIds)
    {
        _logger.LogInformation("Updating processed_data_key_phrases table");

        const string selectSql = "SELECT ConversationId, key_phrases, sentiment, mined_topic as topic, StartTime FROM processed_data";
        await using var selectCommand = new SqlCommand(selectSql, connection);
        await using var reader = await selectCommand.ExecuteReaderAsync();

        var records = new List<Dictionary<string, object>>();
        while (await reader.ReadAsync())
        {
            var record = new Dictionary<string, object>
            {
                ["ConversationId"] = reader.GetString(0),
                ["key_phrases"] = reader.IsDBNull(1) ? "" : reader.GetString(1),
                ["sentiment"] = reader.IsDBNull(2) ? "" : reader.GetString(2),
                ["topic"] = reader.IsDBNull(3) ? "" : reader.GetString(3),
                ["StartTime"] = reader.IsDBNull(4) ? "" : reader.GetString(4)
            };
            records.Add(record);
        }
        await reader.CloseAsync();

        foreach (var record in records.Where(r => conversationIds.Contains(r["ConversationId"].ToString()!)))
        {
            var keyPhrases = record["key_phrases"].ToString()?.Split(',') ?? Array.Empty<string>();
            
            foreach (var keyPhrase in keyPhrases)
            {
                var trimmedKeyPhrase = keyPhrase.Trim();
                if (string.IsNullOrEmpty(trimmedKeyPhrase)) continue;

                const string insertSql = @"
                    INSERT INTO processed_data_key_phrases (ConversationId, key_phrase, sentiment, topic, StartTime) 
                    VALUES (@ConversationId, @key_phrase, @sentiment, @topic, @StartTime)";

                await using var insertCommand = new SqlCommand(insertSql, connection);
                insertCommand.Parameters.AddWithValue("@ConversationId", record["ConversationId"]);
                insertCommand.Parameters.AddWithValue("@key_phrase", trimmedKeyPhrase);
                insertCommand.Parameters.AddWithValue("@sentiment", record["sentiment"]);
                insertCommand.Parameters.AddWithValue("@topic", record["topic"]);
                insertCommand.Parameters.AddWithValue("@StartTime", record["StartTime"]);
                await insertCommand.ExecuteNonQueryAsync();
            }
        }

        _logger.LogInformation("processed_data_key_phrases table updated.");
    }

    private async Task AdjustDatesToCurrentAsync(SqlConnection connection)
    {
        var today = DateTime.Today;

        const string getMaxStartTimeSql = "SELECT MAX(CAST(StartTime AS DATETIME)) FROM [dbo].[processed_data]";
        await using var getMaxCommand = new SqlCommand(getMaxStartTimeSql, connection);
        var maxStartTime = await getMaxCommand.ExecuteScalarAsync() as DateTime?;

        var daysDifference = maxStartTime.HasValue ? (today - maxStartTime.Value).Days - 1 : 0;

        if (daysDifference != 0)
        {
            const string updateProcessedDataSql = @"
                UPDATE [dbo].[processed_data] 
                SET StartTime = FORMAT(DATEADD(DAY, @daysDifference, StartTime), 'yyyy-MM-dd HH:mm:ss'), 
                    EndTime = FORMAT(DATEADD(DAY, @daysDifference, EndTime), 'yyyy-MM-dd HH:mm:ss')";

            await using var updateProcessedDataCommand = new SqlCommand(updateProcessedDataSql, connection);
            updateProcessedDataCommand.Parameters.AddWithValue("@daysDifference", daysDifference);
            await updateProcessedDataCommand.ExecuteNonQueryAsync();

            const string updateKmProcessedDataSql = @"
                UPDATE [dbo].[km_processed_data] 
                SET StartTime = FORMAT(DATEADD(DAY, @daysDifference, StartTime), 'yyyy-MM-dd HH:mm:ss'), 
                    EndTime = FORMAT(DATEADD(DAY, @daysDifference, EndTime), 'yyyy-MM-dd HH:mm:ss')";

            await using var updateKmProcessedDataCommand = new SqlCommand(updateKmProcessedDataSql, connection);
            updateKmProcessedDataCommand.Parameters.AddWithValue("@daysDifference", daysDifference);
            await updateKmProcessedDataCommand.ExecuteNonQueryAsync();

            const string updateKeyPhrasesSql = @"
                UPDATE [dbo].[processed_data_key_phrases] 
                SET StartTime = FORMAT(DATEADD(DAY, @daysDifference, StartTime), 'yyyy-MM-dd HH:mm:ss')";

            await using var updateKeyPhrasesCommand = new SqlCommand(updateKeyPhrasesSql, connection);
            updateKeyPhrasesCommand.Parameters.AddWithValue("@daysDifference", daysDifference);
            await updateKeyPhrasesCommand.ExecuteNonQueryAsync();
        }

        _logger.LogInformation("Dates adjusted to current date.");
    }
}