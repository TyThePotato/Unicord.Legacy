﻿using DiscordLib.EventArgs;
using DiscordLib.Net.Payloads;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SuperSocket.ClientEngine;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WebSocket4Net;

namespace DiscordLib
{
    public class DiscordSocketClient
    {
        private string _token;
        private DiscordClient _client;

        private Uri _gatewayUri;
        private string _sessionId;

        private int _heartbeatInterval;
        private int _skippedHeartbeats;
        private long? _seq;
        private DateTimeOffset _lastHeartbeat;

        private Task _heartbeatTask;
        private CancellationTokenSource _heartbeatCancellation;

        private int _ping;

        private Task _queueTask;
        private ConcurrentQueue<Func<Task>> _taskQueue;

        private WebSocket _webSocket;
        private TaskCompletionSource<object> _tcs;

        internal DiscordSocketClient(DiscordClient client, string token)
        {
            _client = client;
            _token = token;
            _taskQueue = new ConcurrentQueue<Func<Task>>();
        }

        internal async Task ConnectAsync(Uri gatewayUri)
        {
            _gatewayUri = gatewayUri;
            _tcs = new TaskCompletionSource<object>();

            var uri = new UriBuilder(gatewayUri);
            uri.Query = "v=9&encoding=json";

            _webSocket = new WebSocket(uri.ToString());
            _webSocket.Opened += OnSocketOpened;
            _webSocket.MessageReceived += OnSocketMessage;
            _webSocket.Error += OnSocketError;
            _webSocket.Closed += OnSocketClosed;
            _webSocket.Open();

            await _tcs.Task;
        }

        private void OnSocketOpened(object sender, System.EventArgs e)
        {
            _tcs.TrySetResult(null);
        }

        private void OnSocketMessage(object sender, MessageReceivedEventArgs e)
        {
            QueueTask(async () => await HandleSocketMessageAsync(e.Message));
        }

        private async void OnSocketClosed(object sender, System.EventArgs e)
        {
            Debug.WriteLine("Socket closed.");

            _tcs.TrySetCanceled();
            _heartbeatCancellation.Cancel();
            if (_gatewayUri != null)
                 await ConnectAsync(_gatewayUri);
        }

        private void OnSocketError(object sender, ErrorEventArgs e)
        {
            Debug.WriteLine("Socket errored.");
            Debug.WriteLine(e.Exception);
        }

        private async Task HandleSocketMessageAsync(string message)
        {            
#if DEBUG
            Debug.WriteLine("v {0}", message.Length > 256 ? message.Substring(0, 256) : message);
#endif

            var payload = JsonConvert.DeserializeObject<GatewayPayload>(message);
            switch (payload.OpCode)
            {
                case GatewayOpCode.Dispatch:
                    await HandleDispatchAsync(message, payload);
                    break;
                case GatewayOpCode.Heartbeat:
                    break;
                case GatewayOpCode.Reconnect:
                    break;
                case GatewayOpCode.InvalidSession:
                    await HandleInvalidSessionAsync();
                    break;
                case GatewayOpCode.Hello:
                    await HandleHelloAsync(JsonConvert.DeserializeObject<GatewayPayload<HelloPayload>>(message));
                    break;
                case GatewayOpCode.HeartbeatAck:
                    await HandleHeartbeatAckAsync();
                    break;
                default:
                    break;
            }
        }

        private async Task HandleHelloAsync(GatewayPayload<HelloPayload> payload)
        {
            var helloPayload = payload.Data;

            if (_heartbeatCancellation != null && !_heartbeatCancellation.IsCancellationRequested)
                _heartbeatCancellation.Cancel();

            _heartbeatCancellation = new CancellationTokenSource();
            _heartbeatInterval = helloPayload.HeartbeatInterval;
            _heartbeatTask = TaskEx.Run(new Func<Task>(HeartbeatAsync));

            if (_sessionId == null)
            {
                await IdentifyAsync();
            }
            else
            {
                await ResumeAsync();
            }
        }

        private async Task IdentifyAsync()
        {
            var identify = new IdentifyPayload() { Token = _token };
            await SendAsync(GatewayOpCode.Identify, identify);
        }

        private async Task ResumeAsync()
        {
            var resume = new ResumePayload() { Token = _token, SessionId = _sessionId, Sequence = _seq.GetValueOrDefault() };
            await SendAsync(GatewayOpCode.Resume, resume);
        }

        private async Task HandleInvalidSessionAsync()
        {
            _sessionId = null;

            await TaskEx.Delay(new Random().Next(1000, 5000)); // yes this is what you're meant to do lol
            await IdentifyAsync();
        }

        private async Task HeartbeatAsync()
        {
            var token = _heartbeatCancellation.Token;
            while (!token.IsCancellationRequested)
            {
                await SendHeartbeatAsync();
                await TaskEx.Delay(_heartbeatInterval);
            }
        }

        private async Task HandleDispatchAsync(string message, GatewayPayload payload)
        {
            _seq = payload.Sequence;

            switch (payload.EventName.ToLowerInvariant())
            {
                case "ready":
                    await HandleReadyAsync(JsonConvert.DeserializeObject<GatewayPayload<ReadyPayload>>(message));
                    break;
                case "resumed":
                    await HandleResumedAsync(JsonConvert.DeserializeObject<GatewayPayload<object>>(message));
                    break;
                case "guild_create":
                    await HandleGuildCreateAsync(JsonConvert.DeserializeObject<GatewayPayload<Guild>>(message));
                    break;
                case "message_create":
                    await HandleMessageCreateAsync(JsonConvert.DeserializeObject<GatewayPayload<Message>>(message));
                    break;
                case "message_delete":
                    await HandleMessageDeleteAsync(JsonConvert.DeserializeObject<GatewayPayload<MessageDeletePayload>>(message));
                    break;
                default:
                    break;
            }
        }

        private async Task HandleReadyAsync(GatewayPayload<ReadyPayload> payload)
        {
            var readyPayload = payload.Data;
            _sessionId = readyPayload.SessionId;
            _client.CurrentUserSettings = readyPayload.UserSettings;

            foreach (var guild in readyPayload.Guilds)
            {
                guild.Update(guild);
                _client.Guilds.AddOrUpdate(guild.Id, guild, (id, g) => g.Update(guild));
            }

            foreach (var privateChannel in readyPayload.PrivateChannels)
            {
                for (int i = 0; i < privateChannel.Recipients.Count; i++)
                {
                    var recipient = privateChannel.Recipients[i];
                    privateChannel.Recipients[i] = _client.userCache.AddOrUpdate(recipient.Id, recipient, (id, u) => u.Update(recipient));
                }

                privateChannel.Update(privateChannel);
                _client.PrivateChannels.AddOrUpdate(privateChannel.Id, privateChannel, (id, dm) => (PrivateChannel)dm.Update(privateChannel));
            }

            await _client.readyEvent.InvokeAsync();
        }

        private async Task HandleResumedAsync(GatewayPayload<object> payload)
        {
            Debug.WriteLine("Session resumed!");
            await _client.resumedEvent.InvokeAsync();
        }

        private async Task HandleGuildCreateAsync(GatewayPayload<Guild> payload)
        {
            var newGuild = payload.Data;
            var result = _client.Guilds.AddOrUpdate(newGuild.Id, newGuild, (id, guild) => guild.Update(newGuild));

            await _client.guildCreatedEvent.InvokeAsync(new GuildCreatedEventArgs(result));
        }

        private async Task HandleMessageCreateAsync(GatewayPayload<Message> payload)
        {
            var message = payload.Data;
            if (message.ChannelId != 0)
                _client.messageCache.Add(message);

            var ea = new MessageCreateEventArgs() { Message = message, };
            await this._client.messageCreated.InvokeAsync(ea);
        }

        private async Task HandleMessageDeleteAsync(GatewayPayload<MessageDeletePayload> payload)
        {
            var messageId = payload.Data.Id;
            var channelId = payload.Data.ChannelId;
            var guildId = payload.Data.GuildId;

            var channel = _client.GetCachedChannel(channelId);
            var guild = _client.GetCachedGuild(guildId);

            Message msg;
            if (channel == null || !_client.messageCache.TryGet(xm => xm.Id == messageId && xm.ChannelId == channelId, out msg))
                msg = new Message { Id = messageId, ChannelId = channelId, };

            _client.messageCache.Remove(xm => xm.Id == msg.Id && xm.ChannelId == channelId);

            var ea = new MessageDeleteEventArgs() { Message = msg };
            await _client.messageDeleted.InvokeAsync(ea);
        }

        private async Task SendHeartbeatAsync()
        {
            Debug.WriteLine("Sending heartbeat...");
            await SendAsync(GatewayOpCode.Heartbeat, _seq);

            this._lastHeartbeat = DateTimeOffset.Now;
            Interlocked.Increment(ref this._skippedHeartbeats);
        }

        internal Task HandleHeartbeatAckAsync()
        {
            Interlocked.Decrement(ref this._skippedHeartbeats);

            var ping = (int)(DateTime.Now - this._lastHeartbeat).TotalMilliseconds;
            this._ping = ping;

            Debug.WriteLine("Got heartbeat ack in {0}ms!", ping);

            return TaskEx.Delay(0);
        }

        private void QueueTask(Func<Task> task)
        {
            _taskQueue.Enqueue(task);

            if (_queueTask == null || _queueTask.IsCompleted)
                _queueTask = RunQueueAsync();
        }

        private async Task RunQueueAsync()
        {
            Func<Task> task;
            while (_taskQueue.TryDequeue(out task))
            {
                try
                {
                    await task();
                }
                catch (Exception ex)
                {
                    //_logger.LogError(ex, "An error occured in the task queue!");
                    Debug.WriteLine("An error occured in the task queue!, {0}", ex);
                }
            }
        }

        private Task SendAsync<T>(GatewayOpCode op, T data)
        {
            var gatewayPayload = new GatewayPayload<T>() { OpCode = op, Data = data };
            return SendAsync(JsonConvert.SerializeObject(gatewayPayload));
        }

        private Task SendAsync(string message)
        {
            Debug.WriteLine("^ {0}", message);
            return TaskEx.Run(() => _webSocket.Send(message));
        }
    }
}
