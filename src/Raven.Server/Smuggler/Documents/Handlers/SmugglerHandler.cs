// -----------------------------------------------------------------------
//  <copyright file="SmugglerHandler.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using Raven.Client.Documents.Conventions;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Smuggler;
using Raven.Client.Util;
using Raven.Server.Documents;
using Raven.Server.Documents.Operations;
using Raven.Server.Documents.Patch;
using Raven.Server.Json;
using Raven.Server.Routing;
using Raven.Server.ServerWide;
using Raven.Server.ServerWide.Context;
using Raven.Server.Smuggler.Documents.Data;
using Raven.Server.Smuggler.Migration;
using Sparrow.Json;
using Sparrow.Json.Parsing;
using Sparrow.Utils;

namespace Raven.Server.Smuggler.Documents.Handlers
{
    public class SmugglerHandler : DatabaseRequestHandler
    {
        private static readonly HttpClient HttpClient = new HttpClient();

        [RavenAction("/databases/*/smuggler/validate-options", "POST", AuthorizationStatus.ValidUser)]
        public async Task PostValidateOptions()
        {
            using (ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
            using (context.OpenReadTransaction())
            {
                var blittableJson = await context.ReadForMemoryAsync(RequestBodyStream(), "");
                var options = JsonDeserializationServer.DatabaseSmugglerOptions(blittableJson);

                if (!string.IsNullOrEmpty(options.FileName) && options.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    throw new InvalidOperationException($"{options.FileName} is invalid file name");

                if (string.IsNullOrEmpty(options.TransformScript))
                {
                    NoContentStatus();
                    return;
                }

                try
                {
                    var scriptRunner = new ScriptRunner(Database, Database.Configuration, false);
                    scriptRunner.TryCompileScript(string.Format(@"
                    function Transform(docInner){{
                        return ({0}).apply(this, [docInner]);
                    }};", options.TransformScript));
                }
                catch (Exception e)
                {
                    throw new InvalidOperationException("Incorrect transform script", e);
                }

                NoContentStatus();
            }
        }

        [RavenAction("/databases/*/smuggler/export", "POST", AuthorizationStatus.ValidUser)]
        public async Task PostExport()
        {
            using (ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
            {
                var operationId = GetLongQueryString("operationId", false) ?? Database.Operations.GetNextOperationId();
                var startDocumentEtag = GetLongQueryString("startEtag", false) ?? 0;

                var stream = TryGetRequestFromStream("DownloadOptions") ?? RequestBodyStream();


                DatabaseSmugglerOptionsServerSide options;
                using (context.GetManagedBuffer(out var buffer))
                {
                    var firstRead = await stream.ReadAsync(buffer.Buffer.Array, buffer.Buffer.Offset, buffer.Buffer.Count);
                    buffer.Used = 0;
                    buffer.Valid = firstRead;
                    if(firstRead != 0)
                    {
                        var blittableJson = await context.ParseToMemoryAsync(stream, "DownloadOptions", BlittableJsonDocumentBuilder.UsageMode.None, buffer);
                        options = JsonDeserializationServer.DatabaseSmugglerOptions(blittableJson);
                    }
                    else
                    {
                        // no content, we'll use defaults
                        options = new DatabaseSmugglerOptionsServerSide
                        {
                            OperateOnTypes = DatabaseItemType.Documents |
                                             DatabaseItemType.Indexes |
                                             DatabaseItemType.Identities |
                                             DatabaseItemType.Conflicts |
                                             DatabaseItemType.CmpXchg |
                                             DatabaseItemType.RevisionDocuments,
                        };
                    }
                }

                var token = CreateOperationToken();

                var fileName = options.FileName;
                if (string.IsNullOrEmpty(fileName))
                {
                    fileName = $"Dump of {context.DocumentDatabase.Name} {SystemTime.UtcNow.ToString("yyyy-MM-dd HH-mm", CultureInfo.InvariantCulture)}";
                }

                var contentDisposition = "attachment; filename=" + Uri.EscapeDataString(fileName) + ".ravendbdump";
                HttpContext.Response.Headers["Content-Disposition"] = contentDisposition;

                try
                {
                    await Database.Operations.AddOperation(
                            Database,
                            "Export database: " + Database.Name,
                            Operations.OperationType.DatabaseExport,
                            onProgress => Task.Run(() => ExportDatabaseInternal(options, startDocumentEtag, onProgress, context, token), token.Token), operationId, token);
                }
                catch (Exception)
                {
                    HttpContext.Abort();
                }
            }
        }

        private IOperationResult ExportDatabaseInternal(
            DatabaseSmugglerOptionsServerSide options,
            long startDocumentEtag,
            Action<IOperationProgress> onProgress, 
            DocumentsOperationContext context, 
            OperationCancelToken token)
        {
            using (token)
            {
                var source = new DatabaseSource(Database, startDocumentEtag);
                var destination = new StreamDestination(ResponseBodyStream(), context, source);
                var smuggler = new DatabaseSmuggler(Database, source, destination, Database.Time, options, onProgress: onProgress, token: token.Token);
                return smuggler.Execute();
            }
        }

        [RavenAction("/databases/*/admin/smuggler/import-s3-dir", "GET", AuthorizationStatus.Operator)]
        public async Task PostImportFromS3Directory()
        {
            var url = GetQueryStringValueAndAssertIfSingleAndNotEmpty("url");
            var result = await HttpClient.GetAsync(url);
            var dirTextXml = await result.Content.ReadAsStringAsync();
            var filesListing = XElement.Parse(dirTextXml);
            var ns = XNamespace.Get("http://s3.amazonaws.com/doc/2006-03-01/");
            var urls = from content in filesListing.Elements(ns + "Contents")
                let requestUri = url.TrimEnd('/') + "/" + content.Element(ns + "Key").Value
                select (Func<Task<Stream>>) (async () =>
                {
                    var respone = await HttpClient.GetAsync(requestUri);
                    if (respone.IsSuccessStatusCode == false)
                        throw new InvalidOperationException("Request failed on " + requestUri + " with " +
                                                            await respone.Content.ReadAsStreamAsync());
                    return await respone.Content.ReadAsStreamAsync();
                });

            var files = new BlockingCollection<Func<Task<Stream>>>(new ConcurrentQueue<Func<Task<Stream>>>(urls));
            files.CompleteAdding();
            await BulkImport(files, Path.GetTempPath());
        }

        [RavenAction("/databases/*/admin/smuggler/import-dir", "GET", AuthorizationStatus.Operator)]
        public async Task PostImportDirectory()
        {
            var directory = GetQueryStringValueAndAssertIfSingleAndNotEmpty("dir");
            var files = new BlockingCollection<Func<Task<Stream>>>(new ConcurrentQueue<Func<Task<Stream>>>(
                    Directory.GetFiles(directory, "*.dump")
                        .Select(x => (Func<Task<Stream>>)(() => Task.FromResult<Stream>(File.OpenRead(x)))))
            );
            files.CompleteAdding();
            await BulkImport(files, directory);
        }

        private async Task BulkImport(BlockingCollection<Func<Task<Stream>>> files, string directory)
        {
            var results = new ConcurrentQueue<SmugglerResult>();
            var tasks = new Task[Math.Max(1, ProcessorInfo.ProcessorCount / 2)];

            var finalResult = new SmugglerResult();

            for (int i = 0; i < tasks.Length; i++)
            {
                tasks[i] = Task.Run(async () =>
                {
                    while (files.IsCompleted == false)
                    {
                        Func<Task<Stream>> getFile;
                        try
                        {
                            getFile = files.Take();
                        }
                        catch (Exception)
                        {
                            continue;
                        }

                        using (ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
                        using (var file = await getFile())
                        using (var stream = new GZipStream(new BufferedStream(file, 128 * Voron.Global.Constants.Size.Kilobyte), CompressionMode.Decompress))
                        using (var source = new StreamSource(stream, context, Database))
                        {
                            var destination = new DatabaseDestination(Database);

                            var smuggler = new DatabaseSmuggler(Database, source, destination, Database.Time);

                            var result = smuggler.Execute();
                            results.Enqueue(result);
                        }
                    }
                });
            }

            await Task.WhenAll(tasks);

            while (results.TryDequeue(out SmugglerResult importResult))
            {
                finalResult.Documents.SkippedCount += importResult.Documents.SkippedCount;
                finalResult.Documents.ReadCount += importResult.Documents.ReadCount;
                finalResult.Documents.ErroredCount += importResult.Documents.ErroredCount;
                finalResult.Documents.LastEtag = Math.Max(finalResult.Documents.LastEtag, importResult.Documents.LastEtag);
                finalResult.Documents.Attachments = importResult.Documents.Attachments;

                finalResult.RevisionDocuments.ReadCount += importResult.RevisionDocuments.ReadCount;
                finalResult.RevisionDocuments.ErroredCount += importResult.RevisionDocuments.ErroredCount;
                finalResult.RevisionDocuments.LastEtag = Math.Max(finalResult.RevisionDocuments.LastEtag, importResult.RevisionDocuments.LastEtag);
                finalResult.RevisionDocuments.Attachments = importResult.RevisionDocuments.Attachments;

                finalResult.Identities.ReadCount += importResult.Identities.ReadCount;
                finalResult.Identities.ErroredCount += importResult.Identities.ErroredCount;

                finalResult.Indexes.ReadCount += importResult.Indexes.ReadCount;
                finalResult.Indexes.ErroredCount += importResult.Indexes.ErroredCount;

                foreach (var message in importResult.Messages)
                    finalResult.AddMessage(message);
            }

            using (ContextPool.AllocateOperationContext(out DocumentsOperationContext finalContext))
            {
                var memoryStream = new MemoryStream();
                WriteImportResult(finalContext, finalResult, memoryStream);
                memoryStream.Position = 0;
                try
                {
                    using (var output = File.Create(Path.Combine(directory, "smuggler.results.txt")))
                    {
                        memoryStream.CopyTo(output);
                    }
                }
                catch (Exception)
                {
                    // ignore any failure here
                }
                memoryStream.Position = 0;
                memoryStream.CopyTo(ResponseBodyStream());
            }
        }

        [RavenAction("/databases/*/smuggler/import", "GET", AuthorizationStatus.ValidUser)]
        public Task GetImport()
        {
            if (HttpContext.Request.Query.ContainsKey("file") == false &&
                HttpContext.Request.Query.ContainsKey("url") == false)
            {
                throw new ArgumentException("'file' or 'url' are mandatory when using GET /smuggler/import");
            }
            return PostImport();
        }

        [RavenAction("/databases/*/smuggler/import", "POST", AuthorizationStatus.ValidUser)]
        public async Task PostImport()
        {
            using (ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
            {
                var options = DatabaseSmugglerOptionsServerSide.Create(HttpContext, context);

                using (var stream = new GZipStream(new BufferedStream(await GetImportStream(), 128 * Voron.Global.Constants.Size.Kilobyte), CompressionMode.Decompress))
                using (var token = CreateOperationToken())
                using (var source = new StreamSource(stream, context, Database))
                {
                    var destination = new DatabaseDestination(Database);

                    var smuggler = new DatabaseSmuggler(Database, source, destination, Database.Time, options, token: token.Token);

                    var result = smuggler.Execute();

                    WriteImportResult(context, result, ResponseBodyStream());
                }
            }
        }

        [RavenAction("/databases/*/admin/smuggler/migrate", "POST", AuthorizationStatus.Operator)]
        public async Task Migrate()
        {
            using (ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
            {
                var migrationConfiguration = await context.ReadForMemoryAsync(RequestBodyStream(), "migration-configuration");
                var migrationConfigurationJson = JsonDeserializationServer.SingleDatabaseMigrationConfiguration(migrationConfiguration);

                if (string.IsNullOrWhiteSpace(migrationConfigurationJson.ServerUrl))
                    throw new ArgumentException("Url cannot be null or empty");

                if (string.IsNullOrWhiteSpace(migrationConfigurationJson.DatabaseName))
                    throw new ArgumentException("Database name cannot be null or empty");

                var migrator = new Migrator(migrationConfigurationJson, ServerStore, Database.DatabaseShutdown);
                await migrator.UpdateBuildInfoIfNeeded();
                var operationId = migrator.StartMigratingSingleDatabase(migrationConfigurationJson.DatabaseName, Database);

                using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
                {
                    writer.WriteOperationId(context, operationId);
                }
            }
        }

        [RavenAction("/databases/*/smuggler/import/async", "POST", AuthorizationStatus.ValidUser)]
        public async Task PostImportAsync()
        {
            using (ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
            {
                if (HttpContext.Request.HasFormContentType == false)
                {
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.BadRequest; // Bad request
                    using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
                    {
                        context.Write(writer, new DynamicJsonValue
                        {
                            ["Type"] = "Error",
                            ["Error"] = "This endpoint requires form content type"
                        });
                        return;
                    }
                }

                var operationId = GetLongQueryString("operationId", false) ?? Database.Operations.GetNextOperationId();
                var token = CreateOperationToken();

                var result = new SmugglerResult();
                await Database.Operations.AddOperation(Database, "Import to: " + Database.Name,
                    Operations.OperationType.DatabaseImport,
                    onProgress =>
                    {
                        return Task.Run(async () =>
                        {
                            try
                            {
                                var boundary = MultipartRequestHelper.GetBoundary(
                                    MediaTypeHeaderValue.Parse(HttpContext.Request.ContentType),
                                    MultipartRequestHelper.MultipartBoundaryLengthLimit);
                                var reader = new MultipartReader(boundary, HttpContext.Request.Body);
                                DatabaseSmugglerOptionsServerSide options = null;

                                while (true)
                                {
                                    var section = await reader.ReadNextSectionAsync().ConfigureAwait(false);
                                    if (section == null)
                                        break;

                                    if (ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out ContentDispositionHeaderValue contentDisposition) == false)
                                        continue;

                                    if (MultipartRequestHelper.HasFormDataContentDisposition(contentDisposition))
                                    {
                                        var key = HeaderUtilities.RemoveQuotes(contentDisposition.Name);
                                        if (key != "importOptions")
                                            continue;

                                        BlittableJsonReaderObject blittableJson;
                                        if (section.Headers.ContainsKey("Content-Encoding") && section.Headers["Content-Encoding"] == "gzip")
                                        {
                                            using (var gzipStream = new GZipStream(section.Body, CompressionMode.Decompress))
                                            {
                                                blittableJson = await context.ReadForMemoryAsync(gzipStream, "importOptions");
                                            }
                                        }
                                        else
                                        {
                                            blittableJson = await context.ReadForMemoryAsync(section.Body, "importOptions");
                                        }

                                        options = JsonDeserializationServer.DatabaseSmugglerOptions(blittableJson);
                                        continue;
                                    }

                                    if (MultipartRequestHelper.HasFileContentDisposition(contentDisposition) == false)
                                        continue;

                                    var stream = new GZipStream(section.Body, CompressionMode.Decompress);
                                    DoImportInternal(context, stream, options, result, onProgress, token);
                                }
                            }
                            catch (Exception e)
                            {
                                result.AddError($"Error occurred during import. Exception: {e.Message}");
                                throw;
                            }

                            return (IOperationResult)result;
                        });
                    }, operationId, token).ConfigureAwait(false);

                WriteImportResult(context, result, ResponseBodyStream());
            }
        }

        [RavenAction("/databases/*/smuggler/import/csv", "POST", AuthorizationStatus.ValidUser)]
        public async Task ImportFromCsv()
        {
            using (Database.DocumentsStorage.ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
            {
                if (HttpContext.Request.HasFormContentType == false)
                {
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                    using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
                    {
                        context.Write(writer, new DynamicJsonValue
                        {
                            ["Type"] = "Error",
                            ["Error"] = "Import from csv requires form content type"
                        });
                        return;
                    }
                }
                var token = new OperationCancelToken(Database.DatabaseShutdown);
                var result = new SmugglerResult();
                var operationId = GetLongQueryString("operationId", false) ?? Database.Operations.GetNextOperationId();
                var collection = GetStringQueryString("collection", false);
                var operationDescription = collection != null ? "Import collection: " + collection : "Import collection from CSV";
                await Database.Operations.AddOperation(Database, operationDescription, Raven.Server.Documents.Operations.Operations.OperationType.CollectionImportFromCsv,
                    onProgress =>
                    {
                        return Task.Run(async () =>
                        {
                            try
                            {
                                var reader = new MultipartReader(MultipartRequestHelper.GetBoundary(MediaTypeHeaderValue.Parse(HttpContext.Request.ContentType),
                                    MultipartRequestHelper.MultipartBoundaryLengthLimit), HttpContext.Request.Body);
                                while (true)
                                {
                                    var section = await reader.ReadNextSectionAsync().ConfigureAwait(false);
                                    if (section == null)
                                        break;

                                    if (ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out ContentDispositionHeaderValue contentDisposition) == false)
                                        continue;

                                    if (MultipartRequestHelper.HasFileContentDisposition(contentDisposition))
                                    {
                                        if (ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out contentDisposition) == false)
                                            continue;

                                        if (string.IsNullOrEmpty(collection))
                                        {
                                            var fileName = contentDisposition.FileName.ToString().Trim('\"');
                                            collection = Inflector.Pluralize(CSharpClassName.ConvertToValidClassName(Path.GetFileNameWithoutExtension(fileName)));
                                        }
                                        
                                        var options = new DatabaseSmugglerOptionsServerSide();
                                        if (section.Headers.ContainsKey("Content-Encoding") && section.Headers["Content-Encoding"] == "gzip")
                                        {
                                            using (var gzipStream = new GZipStream(section.Body, CompressionMode.Decompress))
                                            {
                                                ImportDocumentsFromCsvStream(gzipStream, context, collection, options, result, onProgress, token);
                                            }
                                        }
                                        else
                                        {
                                            ImportDocumentsFromCsvStream(section.Body, context, collection, options, result, onProgress, token);
                                        }
                                    }
                                }
                            }
                            catch (Exception e)
                            {
                                result.AddError($"Error occurred during csv import. Exception: {e.Message}");
                                throw;
                            }
                            return (IOperationResult)result;
                        });
                    }, operationId, token);
                
                WriteImportResult(context, result, ResponseBodyStream());
            }
        }

        private void ImportDocumentsFromCsvStream(Stream stream, DocumentsOperationContext context, string entity, DatabaseSmugglerOptionsServerSide options, SmugglerResult result, Action<IOperationProgress> onProgress, OperationCancelToken token)
        {
            if (string.IsNullOrEmpty(entity) == false && char.IsLower(entity[0]))
                entity = char.ToUpper(entity[0]) + entity.Substring(1);

            result.AddInfo($"Import collection: {entity}");
            using (var source = new CsvStreamSource(stream, context, entity))
            {
                var destination = new DatabaseDestination(Database);
                var smuggler = new DatabaseSmuggler(Database, source, destination, Database.Time, options, result, onProgress, token.Token);
                smuggler.Execute();
            }
        }

        private void DoImportInternal(DocumentsOperationContext context, Stream stream, DatabaseSmugglerOptionsServerSide options, SmugglerResult result, Action<IOperationProgress> onProgress, OperationCancelToken token)
        {
            using (stream)
            using (token)
            using (var source = new StreamSource(stream, context, Database))
            {
                var destination = new DatabaseDestination(Database);
                var smuggler = new DatabaseSmuggler(Database, source, destination, Database.Time, options, result, onProgress, token.Token);

                smuggler.Execute();
            }
        }

        private async Task<Stream> GetImportStream()
        {
            var file = GetStringQueryString("file", required: false);
            if (string.IsNullOrEmpty(file) == false)
            {
                if(IsOperator() == false)
                    throw new UnauthorizedAccessException("The use of the 'file' query string parameters is limited operators and above");
                return File.OpenRead(file);
            }

            var url = GetStringQueryString("url", required: false);
            if (string.IsNullOrEmpty(url) == false)
            {
                if(IsOperator() == false)
                    throw new UnauthorizedAccessException("The use of the 'url' query string parameters is limited operators and above");
                
                if (HttpContext.Request.Method == "POST")
                {
                    var msg = await HttpClient.PostAsync(url, new StreamContent(HttpContext.Request.Body)
                    {
                        Headers =
                        {
                            ContentType =  new System.Net.Http.Headers.MediaTypeHeaderValue(HttpContext.Request.ContentType)
                        }
                    });
                    return await msg.Content.ReadAsStreamAsync();
                }
                
                return await HttpClient.GetStreamAsync(url);
            }

            return HttpContext.Request.Body;
        }


        private static void WriteImportResult(JsonOperationContext context, SmugglerResult result, Stream stream)
        {
            using (var writer = new BlittableJsonTextWriter(context, stream))
            {
                var json = result.ToJson();
                context.Write(writer, json);
            }
        }
    }
}
