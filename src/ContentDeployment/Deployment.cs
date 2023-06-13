using Azure.Data.Tables;
using Azure.Identity;
using Azure.Storage.Blobs;
using Dapper;
using Content.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Text;

namespace Content
{
    public class Deployment
    {
        private string? _storedCommitId;
        private string? _latestCommitId;

        private readonly string _devOpsToken;
        private readonly string? _devOpsUrl;
        private readonly string? _sqlEndpoint;
        private readonly string? _apiVersion;
        private readonly string? _cosmosDatabase;
        private readonly string? _cosmosContainer;
        private readonly string? _storageEndpoint;
        private readonly string? _storageBlobContainer;

        private readonly CosmosClient _cosmosClient;
        private readonly BlobContainerClient _blobClient;
        private readonly TableClient _tableClient;

        private List<GitStructure>? _moduleContent = new(); // Markdown file contents
        private List<GitStructure>? _moduleAttachments = new(); // .attachments folder for each module
        private List<SQLModuleStructure>? _moduleConfigs = new(); // module.json files within module folder
        private List<SQLCourseStructure>? _courseConfigs = new(); // course.json files within the course folder

        private readonly HttpClient _httpClient = new();
        private readonly ILogger<Deployment> _log;

        public Deployment()
        {
            var cred = new DefaultAzureCredential();
            _log = LoggerFactory.Create(
                    builder => builder
                        .AddSimpleConsole(options =>
                        {
                            options.IncludeScopes = true;
                            options.SingleLine = true;
                            options.TimestampFormat = "HH:mm:ss ";
                        })
                        .AddConsole()
                        .AddDebug()
                        .SetMinimumLevel(LogLevel.Debug))
                .CreateLogger<Deployment>();

            IConfigurationRoot config = new ConfigurationBuilder()
                .AddJsonFile("local.settings.json", optional: true)
                .AddEnvironmentVariables()
                .Build();

            _devOpsToken = Convert.ToBase64String(Encoding.ASCII.GetBytes(string.Format("{0}:{1}", "", config["devOpsToken"])));
            //_devOpsToken = config["devOpsToken"];
            _devOpsUrl = config["devOpsUrl"];
            _sqlEndpoint = config["sqlEndpoint"];
            _apiVersion = config["apiVersion"];
            _cosmosDatabase = config["cosmosDatabase"];
            _cosmosContainer = config["cosmosContainer"];
            _storageEndpoint = config["storageEndpoint"];
            _storageBlobContainer = config["storageBlobContainer"];
            var storageTableContainer = config["storageTableContainer"];

            _cosmosClient = new CosmosClient(config["cosmosEndpoint"], cred);
            _blobClient =
                new BlobServiceClient(new Uri($"https://{_storageEndpoint}.blob.core.windows.net"), cred)
                    .GetBlobContainerClient(_storageBlobContainer);
            _tableClient = new TableClient(new Uri($"https://{_storageEndpoint}.table.core.windows.net"),
                storageTableContainer, cred);
        }

        public async Task Run()
        {
            _log.LogInformation("Starting...");
            try
            {
                var tableEntity = _tableClient.Query<TableEntity>(filter: "PartitionKey eq 'commitid'")
                    .FirstOrDefault();

                _storedCommitId = tableEntity?.GetString("RowKey");
                //_storedCommitId = tableEntity == null ? null : tableEntity.GetString("RowKey");
            }
            catch (Exception e)
            {
                _log.LogCritical(e, "Failed to obtain the stored CommitId");
                throw;
            }

            await FetchGit();

            if (_courseConfigs!.Count > 0 || _moduleConfigs!.Count > 0) await DeployToSql();
            if (_moduleAttachments!.Count > 0) await UploadAttachmentsToBlob();
            if (_moduleContent!.Count > 0) await DeployToCosmos();

            try
            {
                if (!string.IsNullOrEmpty(_storedCommitId))
                    await _tableClient.DeleteEntityAsync("commitid", _storedCommitId);
                var entity = new TableEntity(partitionKey: "commitid", rowKey: _latestCommitId);
                await _tableClient.UpsertEntityAsync(entity);
                _log.LogInformation("All Operations Succeeded.");
            }
            catch (Exception e)
            {
                _log.LogCritical(e, "Failed to update the table storage of the last commit id processed.");
                throw;
            }
        }

        private async Task FetchGit()
        {
            using (_log.BeginScope("[Fetch Git] "))
            {
                _log.LogInformation("Fetching data from Git Repo");

                try
                {
                    // Get the latest commit id
                    using (var request = new HttpRequestMessage(HttpMethod.Get,
                               new Uri($"{_devOpsUrl}/commits?api-version={_apiVersion}&searchCriteria.$top=1")))
                    {
                        request.Headers.Accept.Clear();
                        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", _devOpsToken);

                        var response = await _httpClient.SendAsync(request);
                        if (!response.IsSuccessStatusCode)
                        {
                            Console.WriteLine(response.Content.ReadAsStringAsync());
                            throw new HttpRequestException($"Request was not successful. Status code: {response.StatusCode}. Reason: {response.ReasonPhrase}");
                        }

                        _latestCommitId =
                            JObject.Parse(await response.Content.ReadAsStringAsync())["value"]![0]!["commitId"]!
                                .ToString();
                    }

                    // If there is no stored commit id in the table storage, fetch the latest version of repo
                    if (string.IsNullOrEmpty(_storedCommitId))
                    {
                        var request =
                            new HttpRequestMessage(HttpMethod.Post,
                                new Uri($"{_devOpsUrl}/itemsbatch?api-version={_apiVersion}"));
                        request.Headers.Accept.Clear();
                        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", _devOpsToken);
                        request.Content = new StringContent(
                            "{'itemDescriptors': [{'path': '/','recursionLevel': 'Full'}],'latestProcessedChange': 'true'}",
                            Encoding.UTF8,
                            "application/json"
                        );

                        var response = await _httpClient.SendAsync(request);
                        if (!response.IsSuccessStatusCode)
                        {
                            Console.WriteLine(response.Content.ReadAsStringAsync());
                            throw new HttpRequestException($"Request was not successful. Status code: {response.StatusCode}. Reason: {response.ReasonPhrase}");
                        }

                        var gitContent = JsonConvert.DeserializeObject<List<GitStructure>?>
                                (JObject.Parse(await response.Content.ReadAsStringAsync())["value"]![0]!.ToString())!
                            .Where(file => file.GitObjectType!.Contains("blob") && !file.Path!.Contains("/cicd/")).ToList();

                        foreach (var config in gitContent.Where(file => file.Path!.Contains("course.json")).ToList())
                        {
                            var configParameters =
                                JsonConvert.DeserializeObject<SQLCourseStructure>(await GetContent(config.Path!));

                            // Check if the module with that kind of a name exists.
                            foreach (var module in configParameters!.Modules!)
                            {
                                try
                                {
                                    await GetContent($"/Modules/{module}/module.json");
                                }
                                catch
                                {
                                    _log.LogCritical("Course: {0} // Module '{1}' not found.",
                                        configParameters.CourseName, module);
                                    throw;
                                }
                            }

                            _courseConfigs!.Add(new SQLCourseStructure()
                            {
                                CourseName = configParameters.CourseName,
                                Description = configParameters.Description,
                                Category = configParameters.Category,
                                CourseLength = configParameters.CourseLength,
                                Modules = configParameters.Modules,
                                ObjectId = config.ObjectId,
                                Path = config.Path,
                                CommitId = config.CommitId!,
                                State = "add"
                            });
                        }

                        foreach (var config in gitContent.Where(file => file.Path!.Contains("module.json")).ToList())
                        {
                            var configParameters =
                                JsonConvert.DeserializeObject<SQLModuleStructure>(await GetContent(config.Path!));

                            _moduleConfigs!.Add(new SQLModuleStructure()
                            {
                                ModuleName = configParameters!.ModuleName,
                                Description = configParameters.Description,
                                ModuleVersion = configParameters.ModuleVersion,
                                ObjectId = config.ObjectId,
                                Path = config.Path,
                                CommitId = config.CommitId!,
                                State = "add"
                            });
                        }

                        _moduleContent = gitContent
                            .Where(file => !file.Path!.Contains("README") && file.Path.Contains(".md"))
                            .ToList();

                        var idCheck = _moduleContent.Where(section => section.Id == "-").ToList();
                        if (idCheck.Count > 0)
                        {
                            _log.LogError("Error with file naming. Please check if these files have followed the naming convention: ");
                            foreach (var sectionId in idCheck)
                            {
                                _log.LogError(" L {0}", sectionId.Path);
                            }
                        }

                        _moduleAttachments = gitContent.Where(file =>
                                !file.Path!.Contains("README") && file.Path.Contains(".png") ||
                                file.Path.Contains(".jpg") ||
                                file.Path.Contains(".jpeg"))
                            .ToList();
                        
                        var pathCheck = _moduleAttachments.Where(attachment => attachment.Path!.Contains(" ")).ToList();
                        if (pathCheck.Count > 0)
                        {
                            _log.LogError("Error with file naming. Please check these attachment names for whitespaces:");
                            foreach (var attachment in pathCheck)
                            {
                                _log.LogError(" L {0}", attachment.Path);
                            }
                        }
                        
                        if (pathCheck.Count > 0 || idCheck.Count > 0) throw new ArgumentException();

                        _log.LogInformation("Found {0} courses in Git.", _courseConfigs!.Count);
                        _log.LogInformation("Found {0} modules in Git.", _moduleConfigs!.Count);
                        _log.LogInformation("Found {0} sections in Git.", _moduleContent.Count);
                        _log.LogInformation("Found {0} attachments in Git.", _moduleAttachments.Count);
                    }
                    else // if there is a latest commit id in table storage, get all the changes that have occurred between present and last commit id
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Get,
                            new Uri(
                                $"{_devOpsUrl}/diffs/commits?baseVersion={_storedCommitId}&baseVersionType=commit&targetVersion={_latestCommitId}&targetVersionType=commit&api-version={_apiVersion}"));
                        request.Headers.Accept.Clear();
                        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", _devOpsToken);

                        var response = await _httpClient.SendAsync(request);
                        
                        
                        if (!response.IsSuccessStatusCode)
                        {
                            Console.WriteLine(response.Content.ReadAsStringAsync());
                            throw new HttpRequestException($"Request was not successful. Status code: {response.StatusCode}. Reason: {response.ReasonPhrase}");
                        }

                        var gitContent = JsonConvert.DeserializeObject<List<GitStructure>?>
                                (JObject.Parse(await response.Content.ReadAsStringAsync())["changes"]!.ToString())!
                            .Where(file => file.GitObjectType!.Contains("blob") && !file.Path!.Contains("/cicd/")).ToList();

                        foreach (var config in gitContent.Where(file => file.Path!.Contains("course.json")).ToList())
                        {
                            SQLCourseStructure configParameters;
                            if (config.ChangeType!.Contains("delete"))
                            {
                                configParameters =
                                    JsonConvert.DeserializeObject<SQLCourseStructure>(
                                        await GetContent(config.Path!, true))!;
                            }
                            else
                            {
                                configParameters =
                                    JsonConvert.DeserializeObject<SQLCourseStructure>(
                                        await GetContent(config.Path!))!;

                                // Check if the module with that kind of a name exists.
                                foreach (var module in configParameters.Modules!)
                                {
                                    try
                                    {
                                        await GetContent($"/Modules/{module}/module.json");
                                    }
                                    catch
                                    {
                                        _log.LogCritical("Course: {0} // Module '{1}' not found.",
                                            configParameters.CourseName, module);
                                        throw;
                                    }
                                }
                            }

                            _courseConfigs!.Add(new SQLCourseStructure()
                            {
                                CourseName = configParameters.CourseName,
                                Description = configParameters.Description,
                                Category = configParameters.Category,
                                CourseLength = configParameters.CourseLength,
                                Modules = configParameters.Modules,
                                ObjectId = config.ObjectId,
                                Path = config.Path,
                                CommitId = config.CommitId!,
                                State = config.ChangeType
                            });
                        }

                        foreach (var config in gitContent.Where(file => file.Path!.Contains("module.json")).ToList())
                        {
                            SQLModuleStructure configParameters;
                            if (config.ChangeType!.Contains("delete"))
                            {
                                configParameters =
                                    JsonConvert.DeserializeObject<SQLModuleStructure>(
                                        await GetContent(config.Path!, true))!;
                            }
                            else
                            {
                                configParameters =
                                    JsonConvert.DeserializeObject<SQLModuleStructure>(
                                        await GetContent(config.Path!))!;
                            }

                            _moduleConfigs!.Add(new SQLModuleStructure()
                            {
                                ModuleName = configParameters.ModuleName,
                                Description = configParameters.Description,
                                ModuleVersion = configParameters.ModuleVersion,
                                ObjectId = config.ObjectId,
                                Path = config.Path,
                                CommitId = config.CommitId!,
                                State = config.ChangeType
                            });
                        }

                        _moduleContent = gitContent
                            .Where(file => !file.Path!.Contains("README") && file.Path.Contains(".md"))
                            .ToList();
                        
                        var idCheck = _moduleContent.Where(section => section.Id == "-").ToList();
                        if (idCheck.Count > 0)
                        {
                            _log.LogError("Error with file naming. Please check if these files have followed the naming convention: ");
                            foreach (var sectionId in idCheck)
                            {
                                _log.LogError(" L {0}", sectionId.Path);
                            }
                        }

                        _moduleAttachments = gitContent.Where(file =>
                                !file.Path!.Contains("README") && file.Path.Contains(".png") ||
                                file.Path.Contains(".jpg") ||
                                file.Path.Contains(".jpeg"))
                            .ToList();

                        var pathCheck = _moduleAttachments.Where(attachment => attachment.Path!.Contains(" ")).ToList();
                        if (pathCheck.Count > 0)
                        {
                            _log.LogError("Error with file naming. Please check these attachment names for whitespaces:");
                            foreach (var attachment in pathCheck)
                            {
                                _log.LogError(" L {0}", attachment.Path);
                            }
                        }
                        
                        if (pathCheck.Count > 0 || idCheck.Count > 0) throw new ArgumentException();

                        _log.LogInformation("Found {0} course alterations in Git.", _courseConfigs!.Count);
                        _log.LogInformation("Found {0} module alterations in Git.", _moduleConfigs!.Count);
                        _log.LogInformation("Found {0} section alterations in Git.", _moduleContent.Count);
                        _log.LogInformation("Found {0} attachment alterations in Git.", _moduleAttachments.Count);
                    }
                }
                catch (Exception e)
                {
                    _log.LogCritical(e, "Error while fetching Data from Git.", "");
                    throw;
                }
            }
        }

        private async Task DeployToSql()
        {
            using (_log.BeginScope("[Deploy To SQL] "))
            {
                _log.LogInformation("Deploying data to SQL Database");

                try
                {
                    await using var connection = new SqlConnection(_sqlEndpoint);
                    var courseInsert = _courseConfigs!.Where(module => module.State!.Contains("add")).ToList();
                    var courseEdit = _courseConfigs!.Where(module => module.State!.Contains("edit")).ToList();
                    var courseDelete = _courseConfigs!.Where(module => module.State!.Contains("delete")).ToList();

                    var moduleInsert = _moduleConfigs!.Where(module =>
                        module.State!.Contains("add") ||
                        module.State!.Contains("rename") && !module.State!.Contains("delete")).ToList();
                    var moduleEdit = _moduleConfigs!.Where(module => module.State!.Contains("edit")).ToList();
                    var moduleDelete = _moduleConfigs!.Where(module => module.State!.Contains("delete")).ToList();


                    if (courseInsert.Count > 0)
                    {
                        _log.LogInformation("Found {0} courses to add:", courseInsert.Count);
                        foreach (var course in courseInsert)
                        {
                            // Update the details if there is a module with that moduleid, if not then insert.
                            if (await connection.ExecuteAsync(
                                    "UPDATE [dbo].[Courses] SET CourseName = @CourseName, Description = @Description, CourseLength = @CourseLength, Category = @Category WHERE CourseId = @CourseId;",
                                    course) < 1)
                                await connection.ExecuteAsync(
                                    "INSERT INTO [dbo].[Courses] (CourseId, CourseName, Description, CourseLength, Category) VALUES (@CourseId, @CourseName, @Description, @CourseLength, @Category);",
                                    course);


                            for (int order = 0; order < course.Modules!.Length; order++)
                            {
                                var parameters = new
                                {
                                    Order = order,
                                    CourseId = course.CourseId,
                                    ModuleId = course.Modules[order]
                                };

                                if (await connection.ExecuteAsync(
                                        "UPDATE [ZeroGravity].[tblCoursesModules] SET ModulesOrder = @Order WHERE CoursesCourseId = @CourseId AND ModulesModuleId = @ModuleId;",
                                        parameters) < 1)
                                    await connection.ExecuteAsync(
                                        "INSERT INTO [ZeroGravity].[tblCoursesModules] (CoursesCourseId, ModulesModuleId, ModulesOrder) VALUES (@CourseId, @ModuleId, @Order);",
                                        parameters);
                            }

                            _log.LogInformation(" L New Course: {0}.", course.CourseName);
                        }
                    }

                    if (courseEdit.Count > 0)
                    {
                        _log.LogInformation("Found {0} courses to edit:", courseEdit.Count);
                        foreach (var course in courseEdit)
                        {
                            await connection.ExecuteAsync(
                                "UPDATE [dbo].[Courses] SET CourseName = @CourseName, Description = @Description, CourseLength = @CourseLength, Category = @Category WHERE CourseId = @CourseId;",
                                course);

                            for (int order = 0; order < course.Modules!.Length; order++)
                            {
                                var parameters = new
                                {
                                    Order = order,
                                    CourseId = course.CourseId,
                                    ModuleId = course.Modules[order]
                                };

                                if (await connection.ExecuteAsync(
                                        "UPDATE [ZeroGravity].[tblCoursesModules] SET ModulesOrder = @Order WHERE CoursesCourseId = @CourseId AND ModulesModuleId = @ModuleId;",
                                        parameters) < 1)
                                    await connection.ExecuteAsync(
                                        "INSERT INTO [ZeroGravity].[tblCoursesModules] (CoursesCourseId, ModulesModuleId, ModulesOrder) VALUES (@CourseId, @ModuleId, @Order);",
                                        parameters);
                            }

                            _log.LogInformation(" L Edit Course: {0}.", course.CourseName);
                        }
                    }

                    if (courseDelete.Count > 0)
                    {
                        _log.LogInformation("Found {0} courses to delete:", courseDelete.Count);
                        foreach (var course in courseDelete)
                        {
                            await connection.ExecuteAsync("DELETE FROM [dbo].[Courses] WHERE CourseId = @CourseId;",
                                course);

                            var parameters = new { CourseId = course.CourseId };

                            await connection.ExecuteAsync(
                                "DELETE FROM [ZeroGravity].[tblCoursesModules] WHERE CoursesCourseId = @CourseId;",
                                parameters);

                            _log.LogInformation(" L Delete Course: {0}", course.CourseName);
                        }
                    }

                    if (moduleInsert.Count > 0)
                    {
                        _log.LogInformation("Found {0} modules to add:", moduleInsert.Count);
                        foreach (var module in moduleInsert)
                        {
                            // Update the details if there is a module with that moduleid, if not then insert.
                            if (await connection.ExecuteAsync(
                                    "UPDATE [dbo].[Modules] SET ModuleName = @ModuleName, ModuleVersion = @ModuleVersion, Description = @Description WHERE ModuleId = @ModuleId;",
                                    module) < 1)
                                await connection.ExecuteAsync(
                                    "INSERT INTO [dbo].[Modules] (ModuleId, ModuleName, ModuleVersion, Description) VALUES (@ModuleId, @ModuleName, @ModuleVersion, @Description);",
                                    module);

                            _log.LogInformation(" L New Module: {0}.", module.ModuleName);
                        }
                    }

                    if (moduleEdit.Count > 0)
                    {
                        _log.LogInformation("Found {0} modules to edit:", moduleEdit.Count);

                        foreach (var module in moduleEdit)
                        {
                            await connection.ExecuteAsync(
                                "UPDATE [dbo].[Modules] SET ModuleName = @ModuleName, ModuleVersion = @ModuleVersion, Description = @Description WHERE ModuleId = @ModuleId;",
                                module);
                            _log.LogInformation(" L Edit Module: {0}.", module.ModuleName);
                        }
                    }

                    if (moduleDelete.Count > 0)
                    {
                        _log.LogInformation("Found {0} modules to delete:", moduleDelete.Count);

                        foreach (var module in moduleDelete)
                        {
                            await connection.ExecuteAsync("DELETE FROM [dbo].[Modules] WHERE ModuleId = @ModuleId;",
                                module);

                            var parameters = new { ModuleId = module.ModuleId };
                            await connection.ExecuteAsync(
                                "DELETE FROM [ZeroGravity].[tblCoursesModules] WHERE ModulesModuleId = @ModuleId;",
                                parameters);

                            _log.LogInformation("  L Delete Module: {0}", module.ModuleName);
                        }
                    }
                }
                catch (Exception e)
                {
                    _log.LogCritical(e, "Error while uploading data to SQL.");
                    throw;
                }
            }
        }

        private async Task DeployToCosmos()
        {
            using (_log.BeginScope("[Update Cosmos] "))
            {
                _log.LogInformation("Updating content on Cosmos");

                try
                {
                    Database database = _cosmosClient.GetDatabase(id: _cosmosDatabase);
                    Container container = database.GetContainer(id: _cosmosContainer);

                    foreach (var section in _moduleContent!.Where(module => module.ChangeType != null && !module.ChangeType.Contains("delete"))
                                 .ToList())
                    {
                        
                        var pattern = @"/(?<moduleName>[\w\d']+)/(?<orderPost>[\w\d']+)-(?<sectionName>[\w\d']+)\.md";
                        var info = Regex.Match(section.Path!, pattern);
                        var moduleId = _moduleConfigs!.FirstOrDefault(module => module.Path == section.Path)
                            ?.ModuleId;

                        var sectionMarkdown = await GetContent(section.Path!);
                        
                        CosmosArchitecture newItem = new(
                            id: section.Path!.Replace("/", "-").Replace(".md", "").Replace(" ", "_")[1..],
                            ModuleId: moduleId ?? info.Groups["moduleName"].Value,
                            OrderPost: int.Parse(info.Groups["orderPost"].Value),
                            SectionName: sectionMarkdown.Substring(0, sectionMarkdown.IndexOf(Environment.NewLine)),
                            MdContent: sectionMarkdown[(sectionMarkdown.IndexOf("\n\n") + 2)..]
                                .Replace("./.attachments/",
                                    $"https://{_storageEndpoint}.blob.core.windows.net/{_storageBlobContainer}/Modules/{info.Groups["moduleName"].Value}/.attachments/"),
                            _self: "",
                            CommitId: section.CommitId!
                        );

                        await container.UpsertItemAsync(
                            item: newItem,
                            partitionKey: new PartitionKey(moduleId ?? info.Groups["moduleName"].Value)
                        );
                    }
                }
                catch (Exception e)
                {
                    _log.LogCritical(e, "Error deploying to Cosmos.");
                    throw;
                }

                try
                {
                    Database database = _cosmosClient.GetDatabase(id: _cosmosDatabase);
                    Container container = database.GetContainer(id: _cosmosContainer);

                    foreach (var section in _moduleContent!.Where(module => module.ChangeType!.Contains("delete"))
                                 .ToList())
                    {
                        var pattern = @"/(?<moduleName>[\w\d']+)/(?<orderPost>[\w\d']+)-(?<sectionName>[\w\d']+)\.md";
                        var info = Regex.Match(section.Path!, pattern);
                        var moduleId = _moduleConfigs!.FirstOrDefault(module => module.Path == section.Path)
                            ?.ModuleId;

                        await container.DeleteItemAsync<GitStructure>(
                            section.Path!.Replace("/", "-").Replace(".md", "").Replace(" ", "_")[1..],
                            new PartitionKey(moduleId ?? info.Groups["moduleName"].Value));
                    }
                }
                catch (Exception e)
                {
                    _log.LogCritical(e, "Error Removing from Cosmos.");
                    throw;
                }
            }
        }

        private async Task<string> GetContent(string path, bool deleted = false)
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get,
                    deleted
                        ? new Uri(
                            $"{_devOpsUrl}/items?path={path}&versionDescriptor.versionOptions=PreviousChange&api-version={_apiVersion}")
                        : new Uri($"{_devOpsUrl}/items?path={path}&api-version={_apiVersion}"));
                request.Headers.Accept.Clear();
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", _devOpsToken);

                var response = await _httpClient.SendAsync(request);
                if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Request was not successful. Status code: {response.StatusCode}. Reason: {response.ReasonPhrase}");

                var result = await response.Content.ReadAsStringAsync();
                if (path.Contains(".md")) return result.Replace(System.Environment.NewLine, "\n");

                return result;
            }
            catch
            {
                _log.LogCritical("Error getting file contents for file {0}.", path);
                throw;
            }
        }

        private async Task UploadAttachmentsToBlob()
        {
            if (_moduleAttachments!.Count > 0)
            {
                using (_log.BeginScope("[Upload Attachments] "))
                {
                    _log.LogInformation("Uploading attachments to the storage account");
                    foreach (var attachment in _moduleAttachments
                                 .Where(attachment => attachment.ChangeType.Contains("delete")).ToList())
                    {
                        try
                        {
                            var blobClient = _blobClient.GetBlobClient(attachment.Path);
                            await blobClient.DeleteIfExistsAsync();
                        }
                        catch (Exception e)
                        {
                            _log.LogError(e, "Error deleting image to blob. Attachment: {0}", attachment.Path);
                            throw;
                        }
                    }

                    foreach (var attachment in _moduleAttachments
                                 .Where(attachment => !attachment.ChangeType.Contains("delete")).ToList())
                    {
                        try
                        {
                            var attachmentStream = await GetImageStream(attachment.Path!);
                            var blobClient = _blobClient.GetBlobClient(attachment.Path);
                            await blobClient.UploadAsync(attachmentStream, overwrite: true);
                        }
                        catch (Exception e)
                        {
                            _log.LogError(e, "Error uploading image to blob. Attachment: {0}", attachment.Path);
                            throw;
                        }
                    }
                }
            }
        }

        private async Task<Stream> GetImageStream(string path)
        {
            using (_log.BeginScope("[Image Upload] "))
            {
                try
                {
                    var request =
                        new HttpRequestMessage(HttpMethod.Get,
                            new Uri($"{_devOpsUrl}/items?path={path}&api-version={_apiVersion}"));
                    request.Headers.Accept.Clear();
                    request.Headers.Authorization = new AuthenticationHeaderValue("Basic", _devOpsToken);

                    var response = await _httpClient.SendAsync(request);
                    if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Request was not successful. Status code: {response.StatusCode}. Reason: {response.ReasonPhrase} ");

                    return await response.Content.ReadAsStreamAsync();
                }
                catch (Exception e)
                {
                    _log.LogCritical(e, "Error getting image. Path: {0}.", path);
                    throw;
                }
            }
        }
    }
}