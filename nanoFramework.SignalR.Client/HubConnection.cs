﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections;
using System.Text;
using System.Threading;
using System.Net.WebSockets;
using System.Net.WebSockets.WebSocketFrame;
using nanoFramework.Json;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using nanoFramework.Logging;
using nanoFramework.Logging.Debug;

namespace nanoFramework.SignalR.Client
{
    /// <summary>
    /// A connection used to invoke hub methods on a SignalR Server. And for server to invoke methods on the client. 
    /// This client does not support Streams.
    /// </summary>
    /// <remarks>
    /// A <see cref="HubConnection"/> should be created using <see cref="HubConnection"/>.
    /// Before hub methods can be invoked the connection must be started using <see cref="Start"/>.
    /// Clean up a connection using <see cref="Stop"/>.
    /// </remarks>
    public class HubConnection
    {
        private const string HANDSHAKE_MESSAGE_JSON = @"{""protocol"":""json"",""version"":1}";
        private const string PING_MESSAGE_JSON = "{\"type\": 6}";
        private const string CLOSE_MESSAGE_JSON = "{\"type\":7}";
        private const string CLOSE_MESSAGE_WITH_ERROR_JSON = "{{\"type\":7,\"error\":\"{errorMessage}\"}}";

        private static readonly TimeSpan DefaultServerTimeout = TimeSpan.FromSeconds(30); // Server ping rate is 15 sec, this is 2 times that.
        private static readonly TimeSpan DefaultHandshakeTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan DefaultKeepAliveInterval = TimeSpan.FromSeconds(15);

        // Todo timeout when someting already gone wrong should not happen!
        // Todo what about reconnecting is this implemented and how? Not yet reconnect in constructor working. // Yes but only reconnects if server requested this. 

        private ClientWebSocket _websocketClient;
        private readonly AutoResetEvent _awaitHandsHake = new AutoResetEvent(false);
        private readonly Hashtable _onInvokeHandlers = new Hashtable();
        private readonly AsyncLogic _asyncLogic = new AsyncLogic();
        private Timer _sendHeartBeatTimer;
        private Timer _serverTimeoutTimer;
        private readonly HubConnectionOptions _hubConnectionOptions;
        private readonly ILogger _logger;

        /// <summary>
        /// Indicates the state of the <see cref="HubConnection"/> to the server.
        /// </summary>
        public HubConnectionState State { get; private set; }

        /// <summary>
        /// Gets or sets the server timeout interval for the connection. 
        /// </summary>
        /// <remarks>
        /// The client times out if it hasn't heard from the server for `this` long.
        /// </remarks>
        public TimeSpan ServerTimeout { get; set; } = DefaultServerTimeout;

        /// <summary>
        /// Gets or sets the interval at which the client sends ping messages.
        /// </summary>
        /// <remarks>
        /// Sending any message resets the timer to the start of the interval.
        /// </remarks>
        public TimeSpan KeepAliveInterval { get; set; } = DefaultKeepAliveInterval;

        /// <summary>
        /// Gets or sets the timeout for the initial handshake.
        /// </summary>
        public TimeSpan HandshakeTimeout { get; set; } = DefaultHandshakeTimeout;

        /// <summary>
        /// Occurs when the connection is closed. The connection could be closed due to an error or due to either the server or client intentionally closing the connection without error.
        /// </summary>
        /// <remarks>
        /// If this event was triggered from a connection error, the System.Exception that occurred will be passed in as the sole argument to this handler. If this event was triggered intentionally by either the client or server, then the argument will be null.
        /// </remarks>
        public event SignalrEvent Closed;

        /// <summary>
        ///  Occurs when the Microsoft.AspNetCore.SignalR.Client.HubConnection successfully reconnects after losing its underlying connection.
        /// </summary>
        /// <remarks>
        /// The System.String parameter will be the Microsoft.AspNetCore.SignalR.Client.HubConnection's new ConnectionId or null if negotiation was skipped.
        /// </remarks>
        public event SignalrEvent Reconnected;

        /// <summary>
        ///  Occurs when the Microsoft.AspNetCore.SignalR.Client.HubConnection starts reconnecting after losing its underlying connection.
        /// </summary>
        /// <remarks>
        /// The System.Exception that occurred will be passed in as the sole argument to this handler.
        /// </remarks>
        public event SignalrEvent Reconnecting;

        /// <summary>
        /// The handler to be used for all connection status change.
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="message">The event message arguments <see cref="SignalrEventMessageArgs"/>.</param>
        public delegate void SignalrEvent(object sender, SignalrEventMessageArgs message);

        /// <summary>
        /// The handler to be used for when client methods are invoked by the server.
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="args">The arguments.</param>
        public delegate void OnInvokeHandler(object sender, object[] args);

        /// <summary>
        /// The location of the Signalr Hub Server.
        /// </summary>
        /// <remarks>
        /// Can be set upon initialization of the <see cref="HubConnection"/>.
        /// </remarks>
        public string Uri { get; private set; }

        /// <summary>
        /// Indicates if reconnection to server is enabled.
        /// </summary>
        /// <remarks>
        /// Client will only reconnect if this is indicated by the server close message. 
        /// This can be enabled by setting <see cref="HubConnectionOptions.Reconnect"/> to true.
        /// This reconnect function is experimental and perhaps better handled elsewhere. 
        /// </remarks>
        public bool ReconnectEnabled => _hubConnectionOptions != null && _hubConnectionOptions.Reconnect;

        /// <summary>
        /// Custome headers.
        /// </summary>
        public ClientWebSocketHeaders CustomHeaders { get; private set; } = new ClientWebSocketHeaders();

        /// <summary>
        /// Initializes a new instance of the <see cref="HubConnection"/> class.
        /// </summary>
        /// <param name="uri">Fully Qualified Domain Name of the SignalR Hub server.</param>
        /// <param name="headers">Optional <see cref="ClientWebSocketHeaders"/> for setting custom headers.</param>
        /// <param name="options">Optional <see cref="HubConnectionOptions"/> where extra options can be defined.</param>
        /// <param name="logger">Optional <see cref="ILogger"/> where extra options can be defined.</param>
        public HubConnection(
            string uri,
            ClientWebSocketHeaders headers = null,
            HubConnectionOptions options = null,
            ILogger logger = null) //reconnect enables the client to reconnect if the Signalr server closes with a reconenct request. 
        {
            _hubConnectionOptions = options;
            State = HubConnectionState.Disconnected;
            LogDispatcher.LoggerFactory ??= new DebugLoggerFactory();
            _logger = logger ?? LogDispatcher.LoggerFactory.CreateLogger(nameof(HubConnection));
            if (headers != null) CustomHeaders = headers;

            if (uri.ToLower().StartsWith("http://")) Uri = "ws" + uri.Substring(4, uri.Length - 4);
            else if (uri.ToLower().StartsWith("https://")) Uri = "wss" + uri.Substring(5, uri.Length - 5);
            else Uri = uri;
        }

        /// <summary>
        /// Stops a connection to the server.
        /// </summary>
        /// <param name="errorMessage">Optional error message to will be send to the server</param>
        public void Stop(string errorMessage = null) //error message to be send to server. 
        {
            if (State != HubConnectionState.Disconnected)
            {
                if (State == HubConnectionState.Connected)
                {
                    if (errorMessage == null) SendMessageFromJsonString(CLOSE_MESSAGE_JSON);
                    else SendMessageFromJsonString(string.Format(CLOSE_MESSAGE_WITH_ERROR_JSON, errorMessage));
                }

                State = HubConnectionState.Disconnected;
                HardClose();
                Closed?.Invoke(this, new SignalrEventMessageArgs { Message = "Closed by client" });
            }
        }

        /// <summary>
        /// Starts a connection to the server.
        /// </summary>
        public void Start()
        {
            if (State == HubConnectionState.Disconnected) InternalStart();
            else _logger.LogError($"{Uri} Connect can only be called when Hubconnection is disconnected.");
        }

        /// <summary>
        /// Invokes a hub method on the server using the specified method name and arguments.
        /// Does not wait for a response from the receiver.
        /// </summary>
        /// <param name="methodName">The name of the server method to invoke.</param>
        /// <param name="args">The arguments used to invoke the server method.</param>
        /// <remarks>
        /// This is a fire and forget implementation
        /// </remarks>
        public void SendCore(string methodName, object[] args)
            => SendInvocationMessage(methodName, args);

        /// <summary>
        /// Invokes a hub method on the server using the specified method name, return type and arguments.
        /// </summary>
        /// <param name="methodName">The name of the server method to invoke.</param>
        /// <param name="returnType">The return type of the server method.</param>
        /// <param name="args">The arguments used to invoke the server method.</param>
        /// <param name="timeout">The time in milliseconds the server return should be awaited, The default value 0 uses the <see cref="ServerTimeout" /></param>
        /// <returns>
        /// A <see cref="object"/> response from the server.
        /// </returns>
        /// <remarks>
        /// This is synchronous call that will block your thread, use <see cref="InvokeCoreAsync"/> for a nonblocking asynchronous call. 
        /// </remarks>
        public object InvokeCore(string methodName, Type returnType, object[] args, int timeout = 0)
            => InvokeCoreAsync(methodName, returnType, args, timeout).Value;

        /// <summary>
        /// Invokes a hub method on the server using the specified method name, return type and arguments.
        /// </summary>
        /// <param name="methodName">The name of the server method to invoke.</param>
        /// <param name="returnType">The return type of the server method.</param>
        /// <param name="args">The arguments used to invoke the server method.</param>
        /// <param name="timeout">The time in milliseconds the server return should be awaited, The default value 0 uses the <see cref="ServerTimeout" /></param>
        /// <returns>
        /// A <see cref="AsyncResult"/> that represents the asynchronous invoke.
        /// </returns>
        /// <remarks>
        /// This is an asynchronous call
        /// </remarks>
        public AsyncResult InvokeCoreAsync(string methodName, Type returnType, object[] args, int timeout = 0)
        {
            TimeSpan serverTimeout = new(0, 0, 0, 0, timeout);

            if (timeout == 0) serverTimeout = ServerTimeout;
            else if (timeout == -1) serverTimeout = Timeout.InfiniteTimeSpan;

            var asyncResult = _asyncLogic.BeginAsyncResult(returnType, serverTimeout);

            SendInvocationMessage(methodName, args, asyncResult.InvocationId);

            return asyncResult;
        }

        /// <summary>
        /// Registers a handler that will be invoked when the hub method with the specified method name is invoked.
        /// </summary>
        /// <param name="methodName">The name of the hub method to define.</param>
        /// <param name="parameterTypes">The parameters types expected by the hub method.</param>
        /// <param name="handler">The handler that will be raised when the hub method is invoked.</param>
        /// <returns>A subscription on a hub method.</returns>
        /// <remarks>
        /// This is a low level method for registering a handler.
        /// </remarks>
        public void On(string methodName, Type[] parameterTypes, OnInvokeHandler handler)
        {
            if (_onInvokeHandlers[methodName] != null) _logger.LogError($"Only one handler per method allowed. {methodName} - already exists");

            _onInvokeHandlers.Add(methodName, new object[] { handler, parameterTypes });
        }

        private void InternalStart(bool reconnecting = false)
        {
            State = reconnecting ? HubConnectionState.Reconnecting : HubConnectionState.Connecting;

            // compose client websocket options with what we have
            var clientWebSocketOptions = new ClientWebSocketOptions()
            {
                Certificate = _hubConnectionOptions.Certificate,
                SslVerification = _hubConnectionOptions.SslVerification,
                SslProtocol = _hubConnectionOptions.SslProtocol
            };

            // now create the websocket client with the options
            _websocketClient = new ClientWebSocket(clientWebSocketOptions);

            _websocketClient.MessageReceived += WebsocketClient_MessageReceived;
            _websocketClient.ConnectionClosed += WebSocketClient_Closed;

            string websocketException = string.Empty;

            try
            {
                _websocketClient.Connect(Uri, CustomHeaders);
            }
            catch (Exception ex)
            {
                websocketException = ex.Message;
            }

            if (_websocketClient.State == WebSocketState.Open && string.IsNullOrEmpty(websocketException))
            {
                SendMessageFromJsonString(HANDSHAKE_MESSAGE_JSON);

                var connectTimeout = new Timer(ConnectionHandshake_Timeout, null, (int)HandshakeTimeout.TotalMilliseconds, 0);
                _awaitHandsHake.WaitOne();
                connectTimeout.Dispose();

                // set timouts 
                if (State == HubConnectionState.Connected)
                {
                    _sendHeartBeatTimer = new Timer(SendHeartBeatEvent, null, (int)KeepAliveInterval.TotalMilliseconds, -1);
                    _serverTimeoutTimer = new Timer(ServerTimeoutEvent, null, (int)ServerTimeout.TotalMilliseconds, -1);
                    return;
                }
            }

            if (ReconnectEnabled && !reconnecting) HardClose(true);
            else
            {
                State = HubConnectionState.Disconnected;
                if (string.IsNullOrEmpty(websocketException)) _logger.LogError("Unable to connect");
                else _logger.LogError($"Unable to connect, Websocket error: {websocketException}");
            }
        }

        private void WebSocketClient_Closed(object sender, EventArgs e)
        {
            HardClose();
            Closed?.Invoke(this, new SignalrEventMessageArgs { Message = "Underlying WebSocketClient closed" });
        }

        private void SendHeartBeatEvent(object state) => SendMessageFromJsonString(PING_MESSAGE_JSON);

        private void ServerTimeoutEvent(object state)
        {
            if (State == HubConnectionState.Connected)
            {
                HardClose();
                Closed?.Invoke(this, new SignalrEventMessageArgs { Message = "server timed out" });
            }
        }

        private void ConnectionHandshake_Timeout(object state)
        {
            _awaitHandsHake.Set();
            State = HubConnectionState.Disconnected;
            _logger.LogError("Hubconnection connect timeout");
        }

        public void SendInvocationMessage(string methodName, object[] args, string invocationId = "", string[] streamIds = null)
        {
            var message = !string.IsNullOrEmpty(invocationId) || (streamIds != null && streamIds.Length > 0) ?
                new InvocationBlockingSendMessage() :
                new InvocationSendMessage();
            message.Target = methodName;
            message.Type = MessageType.Invocation;
            message.Arguments = args;
            message.InvocationId = invocationId;
            message.StreamIds = streamIds;

            SendMessageFromJsonString(message.ToString());
        }

        public void SendStreamMessage(string methodName, object[] args, bool inv, string invocationId, string[] streamIds = null)
        {
            var message = new InvocationBlockingSendMessage();
            message.Target = methodName;
            message.Type = inv ? MessageType.StreamInvocation : MessageType.StreamItem;
            message.Arguments = args;
            message.InvocationId = invocationId;
            message.StreamIds = streamIds;

            SendMessageFromJsonString(message.ToString());
        }

        private void WebsocketClient_MessageReceived(object sender, MessageReceivedEventArgs e)
        {
            string[] messages = Encoding.UTF8.GetString(e.Frame.Buffer, 0, e.Frame.Buffer.Length - 1).Split((char)0x1E);

            foreach (var message in messages) _logger.LogTrace($"{Uri} - Received message: {message}");

            if (e.Frame.MessageLength > 0)
            {
                // not a signalr Message!
                if (e.Frame.Buffer[e.Frame.Buffer.Length - 1] != 0x1E) _logger.LogError("Received a non Signalr Message");

                // expect handshake
                if (State == HubConnectionState.Connecting || State == HubConnectionState.Reconnecting)
                {
                    if (e.Frame.Buffer.Length > 3)
                    {
                        var errorMessage = (JsonHubHandshakeError)JsonConvert.DeserializeObject(Encoding.UTF8.GetString(e.Frame.Buffer, 0, e.Frame.Buffer.Length - 1), typeof(JsonHubHandshakeError));
                        if (errorMessage.error != null)
                        {
                            State = HubConnectionState.Disconnected;
                            var websocketClient = (ClientWebSocket)sender;
                            throw new Exception($"{websocketClient.Host + ":" + websocketClient.Port + websocketClient.Prefix} send handshake error: {errorMessage.error}");
                        }
                    }

                    State = HubConnectionState.Connected;
                    _awaitHandsHake.Set();
                }
                else
                {
                    // check for multiple frames in a single message
                    string[] stringMessages = Encoding.UTF8.GetString(e.Frame.Buffer, 0, e.Frame.Buffer.Length - 1).Split((char)0x1E);

                    foreach (string jsonMessage in stringMessages)
                    {
                        var jsonObject = (JsonObject)JsonConvert.Deserialize(jsonMessage);

                        MessageType invocationMessageType = MessageType.Ping;
                        string target = string.Empty;
                        string invocationId = string.Empty;
                        string error = string.Empty;
                        object result = null;
                        bool allowReconnect = false;
                        string[] arguments = default;

                        foreach (var member in jsonObject.Members)
                        {
                            if (member is JsonProperty property)
                            {
                                if (property.Name == "type") invocationMessageType = (MessageType)((JsonValue)property.Value).Value;
                                else if (property.Name == "target") target = ((JsonValue)property.Value).Value.ToString();
                                else if (property.Name == "invocationId") invocationId = ((JsonValue)property.Value).Value.ToString();
                                else if (property.Name == "error") error = ((JsonValue)property.Value).Value.ToString();
                                else if (property.Name == "result") result = ((JsonValue)property.Value).Value;
                                else if (property.Name == "allowReconnect") allowReconnect = (bool)((JsonValue)property.Value).Value;
                                else if (property.Name == "arguments")
                                {
                                    var items = ((JsonArray)property.Value).Items;
                                    arguments = new string[items.Length];
                                    for (int i = 0; i < items.Length; i++)
                                    {
                                        if (items[i] is JsonValue value) arguments[i] = value.Value.ToString();
                                        else if (items[i] is JsonObject obj)
                                        {
                                            arguments[i] = obj.RawValue;
                                            Console.WriteLine(obj.RawValue);
                                        }
                                    }
                                }
                            }
                        }

                        switch (invocationMessageType)
                        {
                            case MessageType.Invocation:
                                if (_onInvokeHandlers[target] is object[] handlerStuff)
                                {
                                    var handler = handlerStuff[0] as OnInvokeHandler;
                                    var types = handlerStuff[1] as Type[];
                                    if (types.Length == arguments.Length)
                                    {
                                        object[] onInvokeArgs = new object[types.Length];
                                        for (int i = 0; i < types.Length; i++)
                                        {
                                            onInvokeArgs[i] = JsonConvert.DeserializeObject(arguments[i], types[i]);
                                        }

                                        handler?.Invoke(this, onInvokeArgs);
                                        break;
                                    }

                                    _logger.LogError("not a valid invocation, types don't match");
                                    break;
                                }

                                _logger.LogInformation($"No matchin method found: {target}");
                                break;
                            case MessageType.Completion:
                                if (error != null && error != string.Empty)
                                {
                                    _asyncLogic.SetAsyncResultError(error, invocationId);
                                }
                                else
                                {
                                    _asyncLogic.SetAsyncResultValue(result, invocationId);
                                }

                                break;
                            case MessageType.Ping:
                                // _ServerTimeoutTimer is already reset after every incoming message. No need to reset the timer here. 
                                break;
                            case MessageType.Close:
                                string errorMessage = string.IsNullOrEmpty(error) ? null : error;
                                if (allowReconnect && ReconnectEnabled)
                                {
                                    // Because underlying Websocket gets closed this will also try to trigger a Hardclose on the Hubconnection also.
                                    // Therefor a reconnect and hardclose can happen simultaneously
                                    Reconnecting?.Invoke(this, new SignalrEventMessageArgs { Message = errorMessage });
                                    HardClose(true);
                                }
                                else
                                {
                                    HardClose();
                                    Closed?.Invoke(this, new SignalrEventMessageArgs { Message = errorMessage });
                                }

                                break;
                            case MessageType.StreamItem:
                            case MessageType.StreamInvocation:
                            case MessageType.CancelInvocation:
                                _logger.LogError("Streaming is not implemented");
                                break;
                            default:
                                _logger.LogError("unknown Signalr Message Type was received");
                                break;
                        }
                    }

                    if (_serverTimeoutTimer != null) _serverTimeoutTimer.Change((int)ServerTimeout.TotalMilliseconds, -1);
                }
            }
        }

        private void SendMessageFromJsonString(string json)
        {
            if (json == null) return;

            _logger.LogTrace($"{Uri} - Message send: {json}");

            if (_websocketClient.State == WebSocketState.Open)
            {
                byte[] messageBytes = new byte[json.Length + 1];
                messageBytes[json.Length] = 0x1E;
                Encoding.UTF8.GetBytes(json, 0, json.Length, messageBytes, 0);
                _websocketClient.Send(messageBytes, WebSocketMessageType.Text);

                if (_sendHeartBeatTimer != null) _sendHeartBeatTimer.Change((int)HandshakeTimeout.TotalMilliseconds, -1);

                return;
            }

            _logger.LogError("Can't send message WebsocketClient is not open");
        }

        private void HardClose(bool reconnect = false)
        {
            _websocketClient.ConnectionClosed -= WebSocketClient_Closed;
            _websocketClient.MessageReceived -= WebsocketClient_MessageReceived;

            State = reconnect ? HubConnectionState.Reconnecting : HubConnectionState.Disconnected;

            _websocketClient?.Close();
            _serverTimeoutTimer?.Dispose();
            _sendHeartBeatTimer?.Dispose();

            if (reconnect)
            {
                int[] retrytimes = new int[] { 0, 2000, 10000, 30000 };
                string errorMesage = null;
                foreach (int time in retrytimes)
                {
                    try
                    {
                        InternalStart(reconnect);
                    }
                    catch (Exception ex)
                    {
                        errorMesage = ex.Message;
                    }

                    if (State == HubConnectionState.Connected)
                    {
                        Reconnected?.Invoke(this, null);
                        return;
                    }
                    else
                    {
                        // can't reconnect so remove all awaiting results.
                        _asyncLogic.CloseAllAsyncResults();
                    }
                }
                State = HubConnectionState.Disconnected;
                Closed?.Invoke(this, new SignalrEventMessageArgs { Message = $"Reconnect failed with message: {errorMesage}" });

            }
            else
            {
                // Closed so remove all awaiting results. 
                _asyncLogic.CloseAllAsyncResults();
            }
        }
    }
}

