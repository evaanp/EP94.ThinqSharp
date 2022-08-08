﻿using EP94.ThinqSharp.Config;
using EP94.ThinqSharp.Interfaces;
using EP94.ThinqSharp.Models;
using EP94.ThinqSharp.Models.Requests;
using EP94.ThinqSharp.Utils;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Exceptions;
using MQTTnet.Packets;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Org.BouncyCastle.Crypto;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;

namespace EP94.ThinqSharp.Clients
{
    internal delegate void StateChanged(ThinqMqttClientState previousState, ThinqMqttClientState newState);
    internal class ThinqMqttClient : IDisposable
    {
        public StateChanged? OnStateChanged;
        public EventHandler<AcSnapshot>? OnSnapshotReceived;
        public ThinqMqttClientState State
        {
            get => _state;
            set
            {
                if (_state != value)
                {
                    ThinqMqttClientState previous = _state;
                    _state = value;
                    OnStateChanged?.Invoke(previous, value);
                }
            }
        }
        private ThinqMqttClientState _state = ThinqMqttClientState.NotConnected;

        private Passport _passport;
        private Route _route;
        private Gateway _gateway;
        private ILogger _logger;
        private ILoggerFactory _loggerFactory;
        private bool _disposed;
        private X509Certificate2? _certificate;
        private Uri _brokerUri;
        private IMqttClient? _client;
        private IEnumerable<string>? _topics;
        private MqttClientOptions? _options;

        private SemaphoreSlim _reconnectSemaphore;
        private volatile bool _disconnectRequested;
        private int _reconnectTimeout = 1;
        private string _clientId;
        private Dictionary<string, ISnapshot> _attachedSnapshots;

        public ThinqMqttClient(Passport passport, Route route, Gateway gateway, ILoggerFactory loggerFactory, string clientId)
        {
            _passport = passport;
            _route = route;
            _gateway = gateway;
            _loggerFactory = loggerFactory;
            _logger = _loggerFactory.CreateLogger<ThinqMqttClient>();
            _brokerUri = new Uri(route.MqttServer);
            _reconnectSemaphore = new SemaphoreSlim(1);
            _clientId = clientId;
            _attachedSnapshots = new Dictionary<string, ISnapshot>();
        }

        public async Task ConnectAsync()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ThinqMqttClient));
            string csr = X509CertificateHelpers.CreateCsr(out AsymmetricCipherKeyPair keyPair);
            RegisterIotCertificateRequest registerIotCertificateRequest = new RegisterIotCertificateRequest(_clientId, _passport, _gateway, _loggerFactory, csr);
            IotCertificate iotCertificate = await registerIotCertificateRequest.RegisterIotCertificateAsync();
            _topics = iotCertificate.Subscriptions;
            using X509Certificate2 certificate = iotCertificate.CertificatePemCertificate;
            _certificate = certificate.CopyWithPrivateKey(keyPair.Private);
            _options = CreateOptions(_certificate, _clientId, _brokerUri);
            _client = new MqttFactory().CreateMqttClient();
            _client.DisconnectedAsync += OnDisconnectAsync;
            _client.ConnectedAsync += OnConnectedAsync;
            _client.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
            _logger.LogInformation("Connecting to mqtt broker {BrokerUri}", _brokerUri);
            _ = _client.ConnectAsync(_options);
        }

        public async Task DisconnectAsync()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ThinqMqttClient));
            _disconnectRequested = true;
            _logger.LogInformation("Disconnecting mqtt client");
            await _reconnectSemaphore.WaitAsync();
            await _client.DisconnectAsync();
            _logger.LogInformation("Disconnected mqtt client");
        }

        public void Attach(string deviceId, ISnapshot snapshot)
        {
            _attachedSnapshots[deviceId] = snapshot;
        }

        public void Detach(string deviceId)
        {
            _attachedSnapshots.Remove(deviceId);
        }

        private Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs args)
        {
            string payload = Encoding.UTF8.GetString(args.ApplicationMessage.Payload);
            _logger.LogDebug("Mqtt message received: {Message}", payload);
            JObject jObject = JObject.Parse(payload);
            if (jObject.TryGetValue(nameof(MqttPayload.Type).ToLower(), out JToken? value) && Equals(value.Value<string>(), "monitoring"))
            {
                MqttPayload? mqttPayload = jObject.ToObject<MqttPayload>();
                if (mqttPayload is null)
                {
                    _logger.LogError("Failed deserializing mqtt payload, message ignored");
                    return Task.CompletedTask;
                }
                if (_attachedSnapshots.TryGetValue(mqttPayload.DeviceId, out ISnapshot? snapshot))
                {
                    snapshot.Merge(mqttPayload.Data.State.Reported);
                }
            }
            else
            {
                _logger.LogDebug("Ignoring payload, not a monitoring message");
            }
           
            return Task.CompletedTask;
        }

        private async Task OnConnectedAsync(MqttClientConnectedEventArgs args)
        {
            State = ThinqMqttClientState.Connected;
            _reconnectTimeout = 1;
            _logger.LogInformation("Connected to mqtt broker {BrokerUri}", _brokerUri);
            if (_topics is null || !_topics.Any())
            {
                _logger.LogError("No topics to subscribe to!");
            }
            foreach (string topic in _topics ?? Array.Empty<string>())
            {
                _logger.LogDebug("Subscribe to topic {Topic}", topic);
                await _client.SubscribeAsync(new MqttTopicFilter() { Topic = topic });
            }
        }

        private async Task OnDisconnectAsync(MqttClientDisconnectedEventArgs args)
        {
            await _reconnectSemaphore.WaitAsync();
            switch (State)
            {
                case ThinqMqttClientState.NotConnected:
                    _logger.LogError("Initial connection failed to mqtt broker {BrokerUri}", _brokerUri);
                    break;

                case ThinqMqttClientState.Connected:
                    _logger.LogError("Disconnected from mqtt broker {BrokerUri}", _brokerUri);
                    break;
            }
            State = ThinqMqttClientState.Disconnected;
            if (_disconnectRequested)
            {
                _logger.LogDebug("Stopped reconnecting because disconnect is requested");
                return;
            }
            if (_client is null || _options is null)
            {
                _logger.LogError("Stopped reconnecting because the mqtt client became null");
                return;
            }
            try
            {
                _reconnectTimeout = Math.Min(_reconnectTimeout * 2, 16);
                await Task.Delay(TimeSpan.FromSeconds(_reconnectTimeout));
                await _client.ConnectAsync(_options);
            }
            catch { }
            _reconnectSemaphore.Release();
        }

        private static MqttClientOptions CreateOptions(X509Certificate2 certificate, string clientId, Uri brokerUri)
        {
            return new MqttClientOptions
            {
                ChannelOptions = new MqttClientTcpOptions
                {
                    Server = brokerUri.Host,
                    Port = brokerUri.Port,
                    TlsOptions = new MqttClientTlsOptions()
                    {
                        UseTls = true,
                        AllowUntrustedCertificates = true,
                        Certificates = new List<X509Certificate>() { certificate },
                        CertificateValidationHandler = (c) =>
                        {
                            return true;
                        },
                        SslProtocol = SslProtocols.None
                    }
                },
                ClientId = clientId
            };
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _certificate?.Dispose();
                    _client?.Dispose();
                    _reconnectSemaphore.Dispose();
                }
                _attachedSnapshots.Clear();
                // TODO: free unmanaged resources (unmanaged objects) and override finalizer
                // TODO: set large fields to null
                _disposed = true;
            }
        }

        // // TODO: override finalizer only if 'Dispose(bool disposing)' has code to free unmanaged resources
        // ~ThinqMqttClient()
        // {
        //     // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        //     Dispose(disposing: false);
        // }

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}