using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using FeatureNinjas.LogPack.Utilities.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileSystemGlobbing.Internal.PatternContexts;

namespace FeatureNinjas.LogPack
{
  public class LogPackMiddleware
    {
        #region Fields

        private readonly RequestDelegate _next;

        private readonly LogPackOptions _options;

        private readonly Dictionary<string, string> _requestBody = new Dictionary<string, string>();

        private static List<string> _stoppedRequests = new List<string>();

        #endregion

        #region Constructors

        public LogPackMiddleware(RequestDelegate next, LogPackOptions options)
        {
            _next = next;
            _options = options;
        }

        #endregion

        #region Methods

        /// <summary>
        /// Reads the request body and resets it afterwards again. Based on
        /// https://www.carlrippon.com/adding-useful-information-to-asp-net-core-web-api-serilog-logs/
        /// </summary>
        /// <param name="httpContext"></param>
        /// <returns></retuns>
        private async Task ReadRequestBody(HttpContext httpContext)
        {
            // Getting the request body is a little tricky because it's a stream
            // So, we need to read the stream and then rewind it back to the beginning
            httpContext.Request.EnableBuffering();
            var body = httpContext.Request.Body;
            var buffer = new byte[Convert.ToInt32(httpContext.Request.ContentLength)];
            await httpContext.Request.Body.ReadAsync(buffer, 0, buffer.Length);
            _requestBody.Add(httpContext.TraceIdentifier, Encoding.UTF8.GetString(buffer));
            body.Seek(0, SeekOrigin.Begin);
            httpContext.Request.Body = body;
        }

        private async Task ReadResponseBody(HttpContext httpContext)
        {
            // The reponse body is also a stream so we need to:
            // - hold a reference to the original response body stream
            // - re-point the response body to a new memory stream
            // - read the response body after the request is handled into our memory stream
            // - copy the response in the memory stream out to the original response stream
            using (var responseBodyMemoryStream = new MemoryStream())
            {
                var originalResponseBodyReference = httpContext.Response.Body;
                httpContext.Response.Body = responseBodyMemoryStream;

                await _next(httpContext);

                httpContext.Response.Body.Seek(0, SeekOrigin.Begin);
                var responseBody = await new StreamReader(httpContext.Response.Body).ReadToEndAsync();
                httpContext.Response.Body.Seek(0, SeekOrigin.Begin);

                await responseBodyMemoryStream.CopyToAsync(originalResponseBodyReference);
            }
        }

        public async Task InvokeAsync(HttpContext context)
        {
            try
            {
                Console.WriteLine("Middleware called");

                try
                {
                    // get the request body first, and reset the stream]
                    await ReadRequestBody(context);

                    // The reponse body is also a stream so we need to:
                    // - hold a reference to the original response body stream
                    // - re-point the response body to a new memory stream
                    // - read the response body after the request is handled into our memory stream
                    // - copy the response in the memory stream out to the original response stream
                    // based on https://www.carlrippon.com/adding-useful-information-to-asp-net-core-web-api-serilog-logs/
                    var responseBody = "";
                    using (var responseBodyMemoryStream = new MemoryStream())
                    {
                        var originalResponseBodyReference = context.Response.Body;
                        context.Response.Body = responseBodyMemoryStream;

                        // call the next middleware, and afterwards create the logpack
                        await _next(context);

                        context.Response.Body.Seek(0, SeekOrigin.Begin);
                        responseBody = await new StreamReader(context.Response.Body).ReadToEndAsync();
                        context.Response.Body.Seek(0, SeekOrigin.Begin);

                        await responseBodyMemoryStream.CopyToAsync(originalResponseBodyReference);
                    }

                    // all other middlewares run now > check the filters and create a logpack
                    if (context.Response?.StatusCode >= 500 && context.Response?.StatusCode < 600)
                    {
                        // handle error codes
                        LogPackTracer.Tracer.Trace(context.TraceIdentifier, "Called middleware returned status code 5xx");

                        if (!_stoppedRequests.Contains(context.TraceIdentifier))
                            await CreateLogPack(context, responseBody);
                    }
                    else
                    {
                        var createLogPackAfterFilter = false;
                        
                        // handle include filters
                        foreach (var includeFilter in _options.Include)
                        {
                            if (includeFilter.Include(context))
                            {
                                LogPackTracer.Tracer.Trace(context.TraceIdentifier, $"Include filter {nameof(includeFilter)} returned true");

                                createLogPackAfterFilter = true;
                                break;
                            }
                        }
                        
                        // handle exclude filter
                        if (createLogPackAfterFilter == true)
                        {
                            foreach (var excludeFilter in _options.Exclude)
                            {
                                if (excludeFilter.Exclude(context))
                                {
                                    LogPackTracer.Tracer.Trace(context.TraceIdentifier, $"Exclude filter {nameof(excludeFilter)}");
    
                                    createLogPackAfterFilter = false;
                                    break;
                                }
                            }
                        }

                        if (createLogPackAfterFilter && !_stoppedRequests.Contains(context.TraceIdentifier))
                            await CreateLogPack(context, responseBody);
                    }
                }
                catch (System.Exception e)
                {
                    LogPackTracer.Tracer.Trace(context.TraceIdentifier, "Middleware ran into an exception:");
                    LogPackTracer.Tracer.Trace(context.TraceIdentifier, e.Message);
                    if (e.StackTrace != null)
                        LogPackTracer.Tracer.Trace(context.TraceIdentifier, e.StackTrace);

                    // creating the logpack failed, don"t call that again
                }
                finally
                {
                    _stoppedRequests.Remove(context.TraceIdentifier);
                    _requestBody.Remove(context.TraceIdentifier);
                    LogPackTracer.Tracer.Remove(context.TraceIdentifier);
                }
            }
            catch (System.Exception exception)
            {
                Console.WriteLine(exception);
                throw;
            }
        }

        private async Task CreateLogPack(HttpContext context, string responseBody)
        {
            await using var stream = new MemoryStream();
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create, true);
            
            // write .logpack file
            var meta = CreateLogPackFile(archive, context);

            // write logs
            CreateFileForLogs(archive, context);

            // write env
            CreateFileForEnv(archive);

            // write the context
            CreateFileForRequest(archive, context);
            
            // write dependencies
            CreateFileForDependencies(archive, context);
            
            // add response in case enabled by the user
            if (_options.IncludeResponse)
            {
                CreateFileForResponse(archive, context, responseBody);
            }

            // add files
            await AddFiles(archive);

            // close the archive
            archive.Dispose();

            // write the zip file
            var fileName = GetLogPackFilename(context);
            using var fileStream = new FileStream(fileName, FileMode.Create);
            stream.Seek(0, SeekOrigin.Begin);
            await stream.CopyToAsync(fileStream);
            await stream.DisposeAsync();
            await fileStream.DisposeAsync();

            // upload to FTP
            if (_options != null && _options.Sinks != null)
            {
                foreach (var sink in _options.Sinks)
                {
                    await sink.Send(fileName);
                }
            }

            // delete the local file
            File.Delete(fileName);
            
            // send notifications out
            foreach (var notificationService in _options.NotificationServices)
            {
                await notificationService.Send(fileName, meta);
            }
        }

        private async Task AddFiles(ZipArchive archive)
        {
            if (_options != null && _options.IncludeFiles != null)
            {
                foreach (var includeFile in _options.IncludeFiles)
                {
                    using var fs = File.OpenRead(includeFile);
                    using var fsr = new StreamReader(fs);

                    var ff = archive.CreateEntry(includeFile);
                    using var entryStream = ff.Open();
                    using var streamWriter = new StreamWriter(entryStream);
                    await streamWriter.WriteAsync(fsr.ReadToEnd());

                    streamWriter.Dispose();
                    entryStream.Dispose();
                }
            }
        }

        private string CreateLogPackFile(ZipArchive archive, HttpContext context)
        {
            if (context == null)
                return "context is null";
            
            // setup the stream
            var file = archive.CreateEntry(".logpack");
            using var entryStream = file.Open();
            using var streamWriter = new StreamWriter(entryStream);
            
            // write the file
            var now = DateTime.Now;
            var meta = new StringBuilder();
            meta.AppendLine($"path: {context.Request.Path.ToString()}");
            meta.AppendLine($"date: {now.ToShortDateString()}");
            meta.AppendLine($"time: {now.ToShortTimeString()}");
            meta.AppendLine($"rc: {context.Response.StatusCode}");
            streamWriter.WriteLine(meta);
            
            // close the stream
            streamWriter.Close();
            entryStream.Close();

            return meta.ToString();
        }

        private void CreateFileForLogs(ZipArchive archive, HttpContext context)
        {
            if (context == null)
                return;

            // setup the stream
            var file = archive.CreateEntry("trace.log");
            using var entryStream = file.Open();
            using var streamWriter = new StreamWriter(entryStream);

            // write the logs
            var logger = LogPackTracer.Tracer;
            foreach (var log in logger.Get(context.TraceIdentifier))
            {
                streamWriter.WriteLine(log);
            }

            // remove the logs from memory
            logger.Remove(context.TraceIdentifier);

            // close the stream
            streamWriter.Close();
            entryStream.Close();
        }

        private void CreateFileForEnv(ZipArchive archive)
        {
            // setup the stream
            var file = archive.CreateEntry("env.log");
            using var entryStream = file.Open();
            using var streamWriter = new StreamWriter(entryStream);

            // write the env variables
            var envVariables = new ProcessStartInfo().EnvironmentVariables;
            foreach (DictionaryEntry env in envVariables)
            {
                streamWriter.WriteLine($"{env.Key}={env.Value}");
            }

            // close the stream
            streamWriter.Close();
            entryStream.Close();
        }

        private void CreateFileForRequest(ZipArchive archive, HttpContext context)
        {
            if (context == null)
                return;

            // setup the stream
            var file = archive.CreateEntry("request");
            using var entryStream = file.Open();
            using var streamWriter = new StreamWriter(entryStream);

            // write the context
            streamWriter.WriteLine($"{context.Request.Protocol} {context.Request.Path.ToString()} {context.Request.Method}");
            streamWriter.WriteLine($"Host: {context.Request.Host.ToString()}");
            streamWriter.WriteLine($"Request.Query:    {context.Request.QueryString}");
            foreach (var requestHeader in context.Request.Headers)
            {
                streamWriter.WriteLine($"{requestHeader.Key}: {requestHeader.Value}");
            }
            
            // get the request body
            if (_options.IncludeRequestPayload && context.Request.Body.CanRead && _requestBody.ContainsKey(context.TraceIdentifier))
            {
                var body = _requestBody[context.TraceIdentifier];
                if (context.Request.ContentType.Equals("application/json"))
                {
                    body = JsonFormatter.FormatJson(body);
                }
                streamWriter.WriteLine(body);
            }
            
            // close the stream
            streamWriter.Close();
            entryStream.Close();
        }
        
        private void CreateFileForResponse(ZipArchive archive, HttpContext context, string responseBody)
        {
            if (context == null)
                return;

            // setup the stream
            var file = archive.CreateEntry("response");
            using var entryStream = file.Open();
            using var streamWriter = new StreamWriter(entryStream);
            
            // write some response info
            streamWriter.WriteLine($"statusCode: {context.Response.StatusCode}");

            // write the context
            foreach (var requestHeader in context.Response.Headers)
            {
                streamWriter.WriteLine($"{requestHeader.Key}: {requestHeader.Value}");
            }
            
            // get the request body
            if (_options.IncludeResponsePayload)
            {
                streamWriter.WriteLine(responseBody);
            }
            
            // close the stream
            streamWriter.Close();
            entryStream.Close();
        }

        private void CreateFileForDependencies(ZipArchive archive, HttpContext context)
        {
            if (context == null)
                return;

            var programAssembly = _options.ProgramType.Assembly;
            if (programAssembly == null)
                return;
            
            // setup the stream
            var file = archive.CreateEntry("deps.log");
            using var entryStream = file.Open();
            using var streamWriter = new StreamWriter(entryStream);

            // write deps to stream
            streamWriter.WriteLine(programAssembly);
            var referencedAssemblies = programAssembly.GetReferencedAssemblies();
            foreach (var referencedAssembly in referencedAssemblies)
            {
                streamWriter.WriteLine("  " + referencedAssembly);
            }

            // close the stream
            streamWriter.Close();
            entryStream.Close();
        }

        private string GetLogPackFilename(HttpContext context)
        {
            var timeUtc = DateTime.UtcNow;
            var time = TimeZoneInfo.ConvertTimeFromUtc(timeUtc, _options.TimeZone);
            var rnd = RandomStringGenerator.RandomString(6);
            var sc = context.Response == null ? 0 : context.Response.StatusCode;
            var filename = $"logpack-{time.ToString("yyyyMMdd-HHmmss")}-{sc}-{rnd}.logpack";
            return filename;
        }

        /// <summary>
        /// When this method is called, then there will be no LogPack created for the given context,
        /// even though the filter criteria would allow to create and upload one.
        /// </summary>
        /// <param name="context"></param>
        public static void Stop(HttpContext context)
        {
            _stoppedRequests.Add(context.TraceIdentifier);
        }

        #endregion
    }
}