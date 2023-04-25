﻿using Microsoft.Extensions.Logging;
using Nexus.DataModel;
using Nexus.Extensibility;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nexus.Sources
{
    [ExtensionDescription(
        "Provides access to remote databases",
        "https://github.com/malstroem-labs/nexus-sources-remote",
        "https://github.com/malstroem-labs/nexus-sources-remote")]
    public partial class Remote : IDataSource, IDisposable
    {
        #region Fields

        private ReadDataHandler? _readData;
        private static readonly int API_LEVEL = 1;
        private RemoteCommunicator _communicator = default!;
        private IJsonRpcServer _rpcServer = default!;

        #endregion

        #region Properties

        /* Possible features to be implemented for this data source:
         * 
         * Transports: 
         *      - anonymous pipes (done)
         *      - named pipes client
         *      - tcp client
         *      - shared memory
         *      - ...
         *      
         * Protocols:
         *      - JsonRpc + binary data stream (done)
         *      - 0mq
         *      - messagepack
         *      - gRPC
         *      - ...
         */

        private DataSourceContext Context { get; set; } = default!;

        #endregion

        #region Methods

        public async Task SetContextAsync(
            DataSourceContext context, 
            ILogger logger,
            CancellationToken cancellationToken)
        {
            Context = context;

            // mode
            var mode = Context.SourceConfiguration?.GetStringValue("mode") ?? "tcp";

            if (mode != "tcp")
                throw new NotSupportedException($"The mode {mode} is not supported.");

            // listen-address
            var listenAddressString = Context.SourceConfiguration?.GetStringValue("listen-address") ?? "0.0.0.0";

            if (!IPAddress.TryParse(listenAddressString, out var listenAddress))
                throw new ArgumentException("The listen-address parameter is not a valid IP-Address.");

            // listen-port
            var listenPortMin = Context.SourceConfiguration?.GetIntValue("listen-port-min") ?? 49152;

            if (!(1 <= listenPortMin && listenPortMin < 65536))
                throw new ArgumentException("The listen-port-min parameter is invalid.");

            var listenPortMax = Context.SourceConfiguration?.GetIntValue("listen-port-max") ?? 65536;

            if (!(1 <= listenPortMin && listenPortMin < 65536))
                throw new ArgumentException("The listen-port-max parameter is invalid.");

            // template
            var templateId = Context.SourceConfiguration?.GetStringValue("template");

            if (templateId is null)
                throw new KeyNotFoundException("The template parameter must be provided.");

            // environment variables
            var requestConfiguration = Context.SourceConfiguration!;
            var environmentVariables = new Dictionary<string, string>();

            if (requestConfiguration.TryGetValue("environment-variables", out var propertyValue) &&
                propertyValue.ValueKind == JsonValueKind.Object)
            {
                var environmentVariablesRaw = propertyValue                    .Deserialize<Dictionary<string, JsonElement>>();
                    
                if (environmentVariablesRaw is not null)
                    environmentVariables = environmentVariablesRaw
                        .Where(entry => entry.Value.ValueKind == JsonValueKind.String)
                        .ToDictionary(entry => entry.Key, entry => entry.Value.GetString() ?? "");
            }

            // Build command
            var actualCommand = BuildCommand(templateId);

            //
            var timeoutTokenSource = GetTimeoutTokenSource(TimeSpan.FromMinutes(1));

            _communicator = new RemoteCommunicator(
                actualCommand,
                environmentVariables, 
                listenAddress,
                listenPortMin,
                listenPortMax,
                HandleReadDataAsync,
                logger);

            _rpcServer = await _communicator.ConnectAsync(timeoutTokenSource.Token);

            var apiVersion = (await _rpcServer.GetApiVersionAsync(timeoutTokenSource.Token)).ApiVersion;

            if (apiVersion < 1 || apiVersion > API_LEVEL)
                throw new Exception($"The API level '{apiVersion}' is not supported.");

            logger.LogTrace("Set context to remote client");

            await _rpcServer
                .SetContextAsync(context, timeoutTokenSource.Token);

            logger.LogDebug("Done preparing remote client");
        }

        public async Task<CatalogRegistration[]> GetCatalogRegistrationsAsync(
            string path,
            CancellationToken cancellationToken)
        {
            var timeoutTokenSource = GetTimeoutTokenSource(TimeSpan.FromMinutes(1));
            cancellationToken.Register(() => timeoutTokenSource.Cancel());

            var response = await _rpcServer
                .GetCatalogRegistrationsAsync(path, timeoutTokenSource.Token);

            return response.Registrations;
        }

        public async Task<ResourceCatalog> GetCatalogAsync(
            string catalogId, 
            CancellationToken cancellationToken)
        {
            var timeoutTokenSource = GetTimeoutTokenSource(TimeSpan.FromMinutes(1));
            cancellationToken.Register(() => timeoutTokenSource.Cancel());

            var response = await _rpcServer
                .GetCatalogAsync(catalogId, timeoutTokenSource.Token);

            return response.Catalog;
        }

        public async Task<(DateTime Begin, DateTime End)> GetTimeRangeAsync(
            string catalogId, 
            CancellationToken cancellationToken)
        {
            var timeoutTokenSource = GetTimeoutTokenSource(TimeSpan.FromMinutes(1));
            cancellationToken.Register(() => timeoutTokenSource.Cancel());

            var response = await _rpcServer
                .GetTimeRangeAsync(catalogId, timeoutTokenSource.Token);

            var begin = response.Begin.ToUniversalTime();
            var end = response.End.ToUniversalTime();

            return (begin, end);
        }

        public async Task<double> GetAvailabilityAsync(
            string catalogId, 
            DateTime begin, 
            DateTime end, 
            CancellationToken cancellationToken)
        {
            var timeoutTokenSource = GetTimeoutTokenSource(TimeSpan.FromMinutes(1));
            cancellationToken.Register(() => timeoutTokenSource.Cancel());

            var response = await _rpcServer
                .GetAvailabilityAsync(catalogId, begin, end, timeoutTokenSource.Token);

            return response.Availability;
        }

        public async Task ReadAsync(
            DateTime begin, 
            DateTime end, 
            ReadRequest[] requests,
            ReadDataHandler readData,
            IProgress<double> progress, 
            CancellationToken cancellationToken)
        {
            _readData = readData;

            try
            {
                var counter = 0.0;

                foreach (var (catalogItem, data, status) in requests)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var timeoutTokenSource = GetTimeoutTokenSource(TimeSpan.FromMinutes(1));
                    cancellationToken.Register(() => timeoutTokenSource.Cancel());

                    var elementCount = data.Length / catalogItem.Representation.ElementSize;

                    await _rpcServer
                        .ReadSingleAsync(begin, end, catalogItem, timeoutTokenSource.Token);

                    await _communicator.ReadRawAsync(data, timeoutTokenSource.Token);
                    await _communicator.ReadRawAsync(status, timeoutTokenSource.Token);

                    progress.Report(++counter / requests.Length);
                }
            }
            finally
            {
                _readData = null;
            }
        }

        private string BuildCommand(string templateId)
        {
            var template = Context.SystemConfiguration?
                .GetStringValue($"{typeof(Remote).FullName}/templates/{templateId}");

            if (template is null)
                throw new Exception($"The template {templateId} does not exist.");

            var command = CommandRegex().Replace(template, match => 
            {
                var parameterKey = match.Groups[1].Value;
                var parameterValue = Context.SourceConfiguration?.GetStringValue(parameterKey);

                if (parameterValue is null)
                    throw new Exception($"The {parameterKey} parameter must be provided.");

                return parameterValue;
            });

            return command;
        }

        private async Task HandleReadDataAsync(string resourcePath, DateTime begin, DateTime end)
        {
            var localReadData = _readData;

            if (localReadData is null)
                throw new InvalidOperationException("Unable to read data without previous invocation of the ReadAsync method.");

            var timeoutTokenSource = GetTimeoutTokenSource(TimeSpan.FromMinutes(1));
            var data = await localReadData(resourcePath, begin, end, timeoutTokenSource.Token);
            var byteData = new CastMemoryManager<double, byte>(MemoryMarshal.AsMemory(data)).Memory;

            await _communicator.WriteRawAsync(byteData, timeoutTokenSource.Token);
        }

        private static CancellationTokenSource GetTimeoutTokenSource(TimeSpan timeout)
        {
            var timeoutToken = new CancellationTokenSource();
            timeoutToken.CancelAfter(timeout);

            return timeoutToken;
        }

        #endregion

        #region IDisposable

        private bool _disposedValue;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    _communicator?.Dispose();
                }

                _disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        [GeneratedRegex("{(.*?)}")]
        private static partial Regex CommandRegex();

        #endregion
    }
}
