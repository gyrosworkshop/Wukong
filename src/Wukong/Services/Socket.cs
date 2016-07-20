using Microsoft.AspNetCore.Http;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Net.WebSockets;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Wukong.Models;
using Microsoft.Extensions.Logging;

namespace Wukong.Services
{
    public interface ISocketManager
    {
        event UserDisconnect UserDisconnect;
        event UserConnect UserConnect;
        void SendMessage(List<string> userIds, WebSocketEvent obj);
        Task AcceptWebsocket(WebSocket webSocket, ClaimsPrincipal user);
    }

    public class SocketManagerMiddleware
    {
        private readonly RequestDelegate _next;
        public SocketManagerMiddleware(RequestDelegate next)
        {
            _next = next;
        }
        public async Task Invoke(HttpContext ctx, ISocketManager socketManager)
        {
            if (ctx.WebSockets.IsWebSocketRequest)
            {
                if (ctx.User?.FindFirst(ClaimTypes.Authentication)?.Value != "true")
                {
                    return;
                }
                var websocket = await ctx.WebSockets.AcceptWebSocketAsync();
                await socketManager.AcceptWebsocket(websocket, ctx.User);
            }
            else
            {
                await _next(ctx);
            }
        }
    }

    public delegate void UserDisconnect(string userId);
    public delegate void UserConnect(string userId);
    public class SocketManager : ISocketManager
    {
        private ILogger Logger;
        public SocketManager(ILoggerFactory loggerFactory)
        {
            Logger = loggerFactory.CreateLogger("SockerManager");
        }

        private Dictionary<string, Timer> disconnectTimer = new Dictionary<string, Timer>();
        public event UserDisconnect UserDisconnect;
        public event UserConnect UserConnect;
        private ConcurrentDictionary<string, WebSocket> verifiedSocket = new ConcurrentDictionary<string, WebSocket>();

        public async Task AcceptWebsocket(WebSocket webSocket, ClaimsPrincipal user)
        {
            var userId = user.FindFirst(ClaimTypes.NameIdentifier).Value;
            ResetTimer(userId);
            verifiedSocket.AddOrUpdate(userId,
                webSocket,
                (key, socket) =>
                {
                    socket.Dispose();
                    return webSocket;
                });
            UserConnect(userId);
            await StartMonitorSocket(userId, webSocket);
        }

        public async void SendMessage(List<string> userIds, WebSocketEvent obj)
        {
            var settings = new JsonSerializerSettings
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver(),
            };
            var message = JsonConvert.SerializeObject(obj, settings);
            var token = CancellationToken.None;
            var type = WebSocketMessageType.Text;
            var data = Encoding.UTF8.GetBytes(message);
            var buffer = new ArraySegment<Byte>(data);
            try 
            {
                await Task.WhenAll(verifiedSocket.Where(i => (i.Value.State == WebSocketState.Open) && userIds.Contains(i.Key))
                                                .Select(i => i.Value.SendAsync(buffer, type, true, token)));
            }
            catch (Exception)
            {
                Logger.LogInformation("user: " + userIds + " message sent failed.");
            }
        }

        private async Task StartMonitorSocket(string userId, WebSocket socket)
        {
            try
            {
                while (socket != null && socket.State == WebSocketState.Open)
                {
                    var token = CancellationToken.None;
                    var buffer = new ArraySegment<Byte>(new Byte[4096]);
                    var received = await socket.ReceiveAsync(buffer, token);
                    switch (received.MessageType)
                    {
                        case WebSocketMessageType.Close:
                            throw new Exception("socket closed");
                        case WebSocketMessageType.Text:
                        case WebSocketMessageType.Binary:
                            Logger.LogError("user: " + userId + " send message, dropped.");
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex.ToString());
                StartDisconnecTimer(userId);
            }
            finally
            {
                Logger.LogDebug("user: " + userId + " socket disposed.");
                WebSocket ws;
                if (verifiedSocket.TryRemove(userId, out ws)) 
                {
                    socket?.Dispose();
                }
            }
        }

        private void StartDisconnecTimer(string userId)
        {
            disconnectTimer[userId] = new Timer(Disconnect, userId, 60 * 1000, 0);
        }

        private void Disconnect(object userId)
        {
            var id = (string)userId;
            UserDisconnect(id);
        }

        private void ResetTimer(string userId)
        {
            if (userId != null && disconnectTimer.ContainsKey(userId))
            {
                var timer = disconnectTimer[userId];
                disconnectTimer.Remove(userId);
                timer.Dispose();
            }
        }
    }
}