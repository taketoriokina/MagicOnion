using ChatApp.Shared.Services;
using MagicOnion;
using MagicOnion.Server;
using MessagePack;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Grpc.Net.Client;
using MagicOnion.Client;
using MicroServer.Shared;
using MagicOnion.Server.OpenTelemetry;

namespace ChatApp.Server
{
    public class ChatService : ServiceBase<IChatService>, IChatService
    {
        private readonly ActivitySource mysqlSource;
        private readonly ActivitySource s2sSource;
        private readonly MagicOnionOpenTelemetryOptions options;
        private readonly ILogger logger;

        public ChatService(BackendActivitySources backendActivity, MagicOnionOpenTelemetryOptions options, ILogger<ChatService> logger)
        {
            this.options = options;
            this.mysqlSource = backendActivity.Get("mysql");
            this.s2sSource = backendActivity.Get("chatapp.server.s2s");
            this.logger = logger;
        }

        public async UnaryResult<Nil> GenerateException(string message)
        {
            var ex = new System.NotImplementedException();
            // dummy external operation.
            using (var activity = this.mysqlSource.StartActivity("errors/insert", ActivityKind.Internal))
            {
                // this is sample. use orm or any safe way.
                activity.SetTag("service.name", options.ServiceName);
                activity.SetTag("table", "errors");
                activity.SetTag("query", $"INSERT INTO rooms VALUES ('{ex.Message}', '{ex.StackTrace}');");
                await Task.Delay(TimeSpan.FromMilliseconds(2));
            }
            throw ex;
        }

        public async UnaryResult<Nil> SendReportAsync(string message)
        {
            logger.LogDebug($"{message}");

            // dummy external operation.
            using (var activity = this.mysqlSource.StartActivity("report/insert", ActivityKind.Internal))
            {
                // this is sample. use orm or any safe way.
                activity.SetTag("service.name", options.ServiceName);
                activity.SetTag("table", "report");
                activity.SetTag("query", $"INSERT INTO report VALUES ('foo', 'bar');");
                await Task.Delay(TimeSpan.FromMilliseconds(2));
            }

            // Server to Server operation
            var channel = GrpcChannel.ForAddress(Environment.GetEnvironmentVariable("Server2ServerEndpoint", EnvironmentVariableTarget.Process) ??  "http://localhost:4999");
            var client = MagicOnionClient.Create<IMessageService>(channel, new[]
            {
                // propagate trace context from ChatApp.Server to MicroServer
                new MagicOnionOpenTelemetryClientFilter(s2sSource, options),
            });
            await client.SendAsync("hello");

            // dummy external operation.
            using (var activity = this.mysqlSource.StartActivity("report/get", ActivityKind.Internal))
            {
                // this is sample. use orm or any safe way.
                activity.SetTag("service.name", options.ServiceName);
                activity.SetTag("table", "report");
                activity.SetTag("query", $"INSERT INTO report VALUES ('foo', 'bar');");
                await Task.Delay(TimeSpan.FromMilliseconds(1));
            }

            return Nil.Default;
        }
    }
}
