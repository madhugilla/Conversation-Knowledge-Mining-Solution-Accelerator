using Microsoft.Extensions.Logging;
using IndexScripts;

// Setup logging
using var loggerFactory = LoggerFactory.Create(builder =>
    builder.AddConsole().SetMinimumLevel(LogLevel.Information));

var logger = loggerFactory.CreateLogger<Program>();

logger.LogInformation("Starting Index Scripts Console Application");

try
{
    // Parse command line arguments
    if (args.Length == 0)
    {
        logger.LogInformation("Usage: IndexScripts.exe <script_name>");
        logger.LogInformation("Available scripts:");
        logger.LogInformation("  01_create_search_index - Creates Azure Search index");
        logger.LogInformation("  02_create_cu_template_audio - Creates Content Understanding analyzer for audio");
        logger.LogInformation("  02_create_cu_template_text - Creates Content Understanding analyzer for text");
        logger.LogInformation("  03_cu_process_data_text - Processes text data");
        logger.LogInformation("  04_cu_process_data_new_data - Processes both text and audio data");
        logger.LogInformation("  all - Runs all scripts in sequence");
        return;
    }

    var scriptName = args[0].ToLowerInvariant();

    switch (scriptName)
    {
        case "01_create_search_index":
            {
                var scriptLogger = loggerFactory.CreateLogger<CreateSearchIndex>();
                var script = new CreateSearchIndex(scriptLogger);
                await script.CreateSearchIndexAsync();
                break;
            }

        case "02_create_cu_template_audio":
            {
                var scriptLogger = loggerFactory.CreateLogger<CreateCuTemplateAudio>();
                var script = new CreateCuTemplateAudio(scriptLogger);
                await script.CreateAnalyzerAsync();
                break;
            }

        case "02_create_cu_template_text":
            {
                var scriptLogger = loggerFactory.CreateLogger<CreateCuTemplateText>();
                var script = new CreateCuTemplateText(scriptLogger);
                await script.CreateAnalyzerAsync();
                break;
            }

        case "03_cu_process_data_text":
            {
                var scriptLogger = loggerFactory.CreateLogger<CuProcessDataText>();
                var script = new CuProcessDataText(scriptLogger);
                await script.ProcessDataAsync();
                break;
            }

        case "04_cu_process_data_new_data":
            {
                var scriptLogger = loggerFactory.CreateLogger<CuProcessDataNewData>();
                var script = new CuProcessDataNewData(scriptLogger);
                await script.ProcessDataAsync();
                break;
            }

        case "all":
            {
                logger.LogInformation("Running all scripts in sequence...");

                // 1. Create search index
                {
                    var scriptLogger = loggerFactory.CreateLogger<CreateSearchIndex>();
                    var script = new CreateSearchIndex(scriptLogger);
                    await script.CreateSearchIndexAsync();
                }

                // 2. Create CU template for audio
                {
                    var scriptLogger = loggerFactory.CreateLogger<CreateCuTemplateAudio>();
                    var script = new CreateCuTemplateAudio(scriptLogger);
                    await script.CreateAnalyzerAsync();
                }

                // 3. Create CU template for text
                {
                    var scriptLogger = loggerFactory.CreateLogger<CreateCuTemplateText>();
                    var script = new CreateCuTemplateText(scriptLogger);
                    await script.CreateAnalyzerAsync();
                }

                // Note: Only run one of the processing scripts at a time
                logger.LogInformation("Setup complete. Run either 03_cu_process_data_text or 04_cu_process_data_new_data separately for data processing.");
                break;
            }

        default:
            logger.LogError("Unknown script: {ScriptName}", scriptName);
            logger.LogInformation("Use 'IndexScripts.exe' without arguments to see available scripts.");
            break;
    }

    logger.LogInformation("Script execution completed successfully.");
}
catch (Exception ex)
{
    logger.LogError(ex, "An error occurred during script execution: {Message}", ex.Message);
    throw;
}
