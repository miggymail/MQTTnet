﻿using MQTTnet.Adapter;
using MQTTnet.Client.Publishing;
using MQTTnet.Client.Receiving;
using MQTTnet.Diagnostics;
using MQTTnet.Exceptions;
using MQTTnet.Protocol;
using MQTTnet.Server.Status;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MQTTnet.Server
{
    public class MqttServer : IMqttServer
    {
        readonly MqttServerEventDispatcher _eventDispatcher;
        readonly ICollection<IMqttServerAdapter> _adapters;
        readonly IMqttNetLogger _rootLogger;
        readonly IMqttNetScopedLogger _logger;

        MqttClientSessionsManager _clientSessionsManager;
        IMqttRetainedMessagesManager _retainedMessagesManager;
        CancellationTokenSource _cancellationTokenSource;

        public MqttServer(IEnumerable<IMqttServerAdapter> adapters, IMqttNetLogger logger)
        {
            if (adapters == null) throw new ArgumentNullException(nameof(adapters));
            _adapters = adapters.ToList();

            if (logger == null) throw new ArgumentNullException(nameof(logger));
            _logger = logger.CreateScopedLogger(nameof(MqttServer));
            _rootLogger = logger;

            _eventDispatcher = new MqttServerEventDispatcher(logger);
        }

        public bool IsStarted => _cancellationTokenSource != null;

        public IMqttServerStartedHandler StartedHandler { get; set; }

        public IMqttServerStoppedHandler StoppedHandler { get; set; }

        public IMqttServerClientConnectedHandler ClientConnectedHandler
        {
            get => _eventDispatcher.ClientConnectedHandler;
            set => _eventDispatcher.ClientConnectedHandler = value;
        }

        public IMqttServerClientDisconnectedHandler ClientDisconnectedHandler
        {
            get => _eventDispatcher.ClientDisconnectedHandler;
            set => _eventDispatcher.ClientDisconnectedHandler = value;
        }

        public IMqttServerClientSubscribedTopicHandler ClientSubscribedTopicHandler
        {
            get => _eventDispatcher.ClientSubscribedTopicHandler;
            set => _eventDispatcher.ClientSubscribedTopicHandler = value;
        }

        public IMqttServerClientUnsubscribedTopicHandler ClientUnsubscribedTopicHandler
        {
            get => _eventDispatcher.ClientUnsubscribedTopicHandler;
            set => _eventDispatcher.ClientUnsubscribedTopicHandler = value;
        }

        public IMqttApplicationMessageReceivedHandler ApplicationMessageReceivedHandler
        {
            get => _eventDispatcher.ApplicationMessageReceivedHandler;
            set => _eventDispatcher.ApplicationMessageReceivedHandler = value;
        }

        public IMqttServerOptions Options { get; private set; }

        public Task<IList<IMqttClientStatus>> GetClientStatusAsync()
        {
            return _clientSessionsManager.GetClientStatusAsync();
        }

        public Task<IList<IMqttSessionStatus>> GetSessionStatusAsync()
        {
            return _clientSessionsManager.GetSessionStatusAsync();
        }

        public Task<IList<MqttApplicationMessage>> GetRetainedApplicationMessagesAsync()
        {
            return _retainedMessagesManager.GetMessagesAsync();
        }

        public Task SubscribeAsync(string clientId, ICollection<MqttTopicFilter> topicFilters)
        {
            if (clientId == null) throw new ArgumentNullException(nameof(clientId));
            if (topicFilters == null) throw new ArgumentNullException(nameof(topicFilters));

            return _clientSessionsManager.SubscribeAsync(clientId, topicFilters);
        }

        public Task UnsubscribeAsync(string clientId, ICollection<string> topicFilters)
        {
            if (clientId == null) throw new ArgumentNullException(nameof(clientId));
            if (topicFilters == null) throw new ArgumentNullException(nameof(topicFilters));

            return _clientSessionsManager.UnsubscribeAsync(clientId, topicFilters);
        }

        public Task<MqttClientPublishResult> PublishAsync(MqttApplicationMessage applicationMessage, CancellationToken cancellationToken)
        {
            if (applicationMessage == null) throw new ArgumentNullException(nameof(applicationMessage));

            MqttTopicValidator.ThrowIfInvalid(applicationMessage.Topic);

            if (_cancellationTokenSource == null) throw new InvalidOperationException("The server is not started.");

            _clientSessionsManager.DispatchApplicationMessage(applicationMessage, null);

            return Task.FromResult(new MqttClientPublishResult());
        }

        public async Task StartAsync(IMqttServerOptions options)
        {
            Options = options ?? throw new ArgumentNullException(nameof(options));

            if (Options.RetainedMessagesManager == null) throw new MqttConfigurationException("options.RetainedMessagesManager should not be null.");

            if (_cancellationTokenSource != null) throw new InvalidOperationException("The server is already started.");

            _cancellationTokenSource = new CancellationTokenSource();

            _retainedMessagesManager = Options.RetainedMessagesManager;
            await _retainedMessagesManager.Start(Options, _rootLogger);
            await _retainedMessagesManager.LoadMessagesAsync().ConfigureAwait(false);

            _clientSessionsManager = new MqttClientSessionsManager(Options, _retainedMessagesManager, _cancellationTokenSource.Token, _eventDispatcher, _rootLogger);
            _clientSessionsManager.Start();

            foreach (var adapter in _adapters)
            {
                adapter.ClientHandler = OnHandleClient;
                await adapter.StartAsync(Options).ConfigureAwait(false);
            }

            _logger.Info("Started.");

            var startedHandler = StartedHandler;
            if (startedHandler != null)
            {
                await startedHandler.HandleServerStartedAsync(EventArgs.Empty).ConfigureAwait(false);
            }
        }

        public async Task StopAsync()
        {
            try
            {
                if (_cancellationTokenSource == null)
                {
                    return;
                }

                await _clientSessionsManager.StopAsync().ConfigureAwait(false);

                _cancellationTokenSource.Cancel(false);

                foreach (var adapter in _adapters)
                {
                    adapter.ClientHandler = null;
                    await adapter.StopAsync().ConfigureAwait(false);
                }

                _logger.Info("Stopped.");
            }
            finally
            {
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;

                _retainedMessagesManager = null;

                _clientSessionsManager?.Dispose();
                _clientSessionsManager = null;
            }

            var stoppedHandler = StoppedHandler;
            if (stoppedHandler != null)
            {
                await stoppedHandler.HandleServerStoppedAsync(EventArgs.Empty).ConfigureAwait(false);
            }
        }

        public Task ClearRetainedApplicationMessagesAsync()
        {
            return _retainedMessagesManager?.ClearMessagesAsync();
        }

        Task OnHandleClient(IMqttChannelAdapter channelAdapter)
        {
            return _clientSessionsManager.HandleClientConnectionAsync(channelAdapter);
        }
    }
}
