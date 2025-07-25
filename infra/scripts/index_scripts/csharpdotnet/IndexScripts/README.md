# Index Scripts - C# Console Application

This is a C# console application that provides equivalent functionality to the Python scripts in the parent directory. All scripts have been converted to C# while maintaining the same core functionality.

## Project Structure

```
csharpdotnet/IndexScripts/
├── Program.cs                          # Main entry point and command orchestration
├── AzureContentUnderstandingClient.cs  # Custom HTTP client for Azure Content Understanding API
├── CreateSearchIndex.cs                # Equivalent to 01_create_search_index.py
├── CreateCuTemplateAudio.cs            # Equivalent to 02_create_cu_template_audio.py
├── CreateCuTemplateText.cs             # Equivalent to 02_create_cu_template_text.py
├── CuProcessDataText.cs                # Equivalent to 03_cu_process_data_text.py
├── CuProcessDataNewData.cs             # Equivalent to 04_cu_process_data_new_data.py
├── IndexScripts.csproj                 # Project file with NuGet package references
├── ckm-analyzer_config_audio.json     # Configuration for audio analyzer
├── ckm-analyzer_config_text.json      # Configuration for text analyzer
├── sample_*.json                       # Sample data files
└── README.md                           # This file
```

## Prerequisites

- .NET 8.0 SDK
- Azure subscription with appropriate services configured:
  - Azure Key Vault with required secrets
  - Azure Cognitive Search
  - Azure OpenAI or OpenAI API
  - Azure SQL Database
  - Azure Data Lake Storage
  - Azure Content Understanding service

## Building the Application

```bash
cd infra/scripts/index_scripts/csharpdotnet/IndexScripts
dotnet restore
dotnet build
```

## Running the Application

### View Available Commands
```bash
dotnet run
```

### Run Individual Scripts
```bash
# Create Azure Search index with vector search capabilities
dotnet run 01_create_search_index

# Create Content Understanding analyzer for audio files
dotnet run 02_create_cu_template_audio

# Create Content Understanding analyzer for text files
dotnet run 02_create_cu_template_text

# Process text transcripts (main data processing)
dotnet run 03_cu_process_data_text

# Process both text and audio data (enhanced processing)
dotnet run 04_cu_process_data_new_data

# Run setup scripts in sequence (excludes data processing)
dotnet run all
```

## Script Descriptions

### 1. CreateSearchIndex (01_create_search_index)
- Creates an Azure Cognitive Search index with vector search capabilities
- Configures semantic search using Azure OpenAI embeddings
- Sets up HNSW algorithm for vector similarity search

### 2. CreateCuTemplateAudio (02_create_cu_template_audio)
- Creates a Content Understanding analyzer for processing audio files
- Uses the `ckm-audio` analyzer ID with audio-specific configuration
- Configures the analyzer for conversation analytics on audio data

### 3. CreateCuTemplateText (02_create_cu_template_text)
- Creates a Content Understanding analyzer for processing text files
- Uses the `ckm-json` analyzer ID with text-specific configuration
- Configures the analyzer for conversation analytics on text transcripts

### 4. CuProcessDataText (03_cu_process_data_text)
- Main data processing pipeline for text transcripts
- Processes files from Azure Data Lake Storage
- Performs content understanding analysis on conversation data
- Generates embeddings and stores processed data in SQL Database
- Uploads searchable documents to Azure Search
- Includes GPT-4 powered topic mining and mapping
- Loads sample data for demonstration

### 5. CuProcessDataNewData (04_cu_process_data_new_data)
- Enhanced version that processes both text and audio data
- Recreates the search index before processing
- Handles multiple data types (JSON transcripts and WAV audio files)
- Includes all functionality from script 3 plus audio processing capabilities

## Configuration

The application uses Azure Key Vault to retrieve configuration values. Update the following constants in each script file:

```csharp
private const string KeyVaultName = "your-key-vault-name";
private const string ManagedIdentityClientId = "your-managed-identity-client-id";
```

### Required Key Vault Secrets

- `AZURE-SEARCH-ENDPOINT` - Azure Cognitive Search service endpoint
- `AZURE-OPENAI-ENDPOINT` - Azure OpenAI service endpoint
- `AZURE-OPENAI-EMBEDDING-MODEL` - Name of the embedding model deployment
- `AZURE-OPENAI-DEPLOYMENT-MODEL` - Name of the chat completion model deployment
- `AZURE-OPENAI-PREVIEW-API-VERSION` - API version for Azure OpenAI
- `AZURE-OPENAI-CU-ENDPOINT` - Azure Content Understanding service endpoint
- `ADLS-ACCOUNT-NAME` - Azure Data Lake Storage account name
- `SQLDB-SERVER` - SQL Database server name
- `SQLDB-DATABASE` - SQL Database name

## Dependencies

The application uses the following NuGet packages:

- `Azure.Identity` - Azure authentication
- `Azure.Security.KeyVault.Secrets` - Key Vault access
- `Azure.Search.Documents` - Azure Cognitive Search
- `Azure.Storage.Files.DataLake` - Azure Data Lake Storage
- `Microsoft.Data.SqlClient` - SQL Server connectivity
- `Azure.AI.OpenAI` - Azure OpenAI integration
- `OpenAI` - OpenAI API client
- `System.Text.Json` - JSON serialization
- `Microsoft.Extensions.Logging` - Logging framework

## Authentication

The application uses Azure Managed Identity for authentication. Ensure that:

1. The application is running in an environment with Managed Identity enabled (Azure VM, App Service, etc.)
2. The Managed Identity has appropriate permissions to:
   - Read secrets from Key Vault
   - Access Azure Cognitive Search
   - Read/write to Azure Data Lake Storage
   - Connect to SQL Database
   - Use Azure OpenAI services
   - Access Content Understanding services

## Error Handling

Each script includes comprehensive error handling and logging. Check the console output for detailed information about execution progress and any issues encountered.

## Differences from Python Version

- Uses Azure SDK for .NET APIs instead of Python equivalents
- Implements async/await patterns throughout for better performance
- Uses C# language features like LINQ for data processing
- Maintains the same logical flow and functionality as the Python scripts
- Uses structured logging with Microsoft.Extensions.Logging

## Troubleshooting

1. **Build Issues**: Ensure .NET 8.0 SDK is installed and all NuGet packages are restored
2. **Authentication Errors**: Verify Managed Identity configuration and permissions
3. **Key Vault Access**: Check that secrets exist and naming matches exactly
4. **API Compatibility**: Ensure Azure service API versions are compatible with the SDK versions used

## Contributing

When modifying the scripts:

1. Maintain the same logical structure as the Python equivalents
2. Use appropriate async/await patterns
3. Include comprehensive error handling and logging
4. Update this README if new dependencies or configuration is required