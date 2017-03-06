using System;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Reflection;

namespace UnityGameServer.Networking {
    public class AsyncServer {
        public delegate void CMD(SocketUser user, Data data);

        public class SocketUser {
            public const int BufferSize = 1024;

            public IUser User;
            public string SessionToken { get; set; }
            public int Permission { get; set; }
            public bool Connected { get; set; }
            public bool IsAuthenticated { get; set; }
            public bool Receiving { get; private set; }
            public bool UdpEnabled { get; set; }
            public int UdpID { get; set; }
            public bool CloseMessage { get; set; }
            public System.Security.Cryptography.RSAParameters RSAKeyInfo { get; private set; }

            public IPEndPoint TcpEndPoint { get; set; }
            public IPEndPoint UdpEndPoint { get; set; }

            private Stopwatch timeOutWatch;

            private TcpClient _client;
            private NetworkStream _stream;

            private AsyncServer _server;

            private CancellationTokenSource cts;
            private CancellationToken token;

            public SocketUser(AsyncServer server, TcpClient client) {
                _server = server;
                _client = client;
                _stream = client.GetStream();
                SessionToken = HashHelper.RandomKey(8);
                TcpEndPoint = (IPEndPoint)client.Client.RemoteEndPoint;
                UdpEndPoint = (IPEndPoint)client.Client.RemoteEndPoint;
                timeOutWatch = new Stopwatch();
                timeOutWatch.Start();
                UdpID = -1;
                Permission = 0;
                Connected = true;
                IsAuthenticated = false;
                UdpEnabled = false;
                cts = new CancellationTokenSource();
                token = cts.Token;
            }

            public async Task<bool> HandleStartConnect() {
                try {
                    byte[] message = await _stream.ReadMessage();
                    if (message == null) {
                        Close(false, "login message null!");
                        return false;
                    }

                    return (message.Length == 2 && message[0] == 0xff && message[1] == 0x01);
                }
                catch (Exception ex) {
                    Logger.LogError("{0}: {1}\n{2}", ex.GetType(), ex.Message, ex.StackTrace);
                }
                return false;
            }

            public async Task HandleMessages() {
                string closeMessage = string.Empty;
                try {
                    Task<byte[]> readTask = _stream.ReadMessage();
                    while(Connected && !token.IsCancellationRequested && !_server.token.IsCancellationRequested) {
                        await Task.WhenAny(readTask);
                        if (readTask != null && readTask.IsCompleted) {
                            byte[] message = readTask.GetAwaiter().GetResult();
                            if (message == null) {
                                closeMessage = "Connection closed by peer.";
                                return; // client closed.
                            }
                            //User.SessionTimerReset();
                            ProcessReceiveBuffer(message, Protocal.Tcp);
                            readTask = _stream.ReadMessage();
                        }
                        else
                            break;
                    }
                }
                catch(IOException) {
                    // nothing, just close in finally.
                }
                catch(ObjectDisposedException) {
                    // nothing, just close in finally.
                }
                catch (Exception ex) {
                    Logger.LogError("{0}: {1}\n{2}", ex.GetType(), ex.Message, ex.StackTrace);
                }
                finally {
                    Close(false, closeMessage);
                }
            }

            public void EnableUdp(int port) {
                UdpEndPoint.Port = port;
                //Logger.Log("UDP end point: {0}:{1}", udpEndPoint.Address.ToString(), udpEndPoint.Port);
                UdpEnabled = true;
            }

            public void SetUser(IUser user) {
                User = user;
                if (User != null)
                    User.UserSet(this);
            }

            public void Send(byte command, string message, Protocal type = Protocal.Tcp) {
                Send(command, Encoding.UTF8.GetBytes(message), type);
            }

            public void Send(byte cmd, byte[] data, Protocal type = Protocal.Tcp) {
                Send(BufferUtils.AddFirst(cmd, data), type);
            }

            public void Send(byte[] data, Protocal type = Protocal.Tcp) {
                if (!Connected)
                    return;

                try {
                    if (type == Protocal.Tcp || !UdpEnabled) {
                        if (_stream != null && data != null)
                            _stream.SendMessasge(data);
                    }
                    else {
                        Instance.SendUdp(data, UdpEndPoint);
                    }
                }
                catch (IOException) {
                    Close(false, "Send IOException");
                }
                catch (SocketException) {
                    Close(false, "Send SocketException");
                }
                catch (Exception ex) {
                    Logger.LogError("{0}: {1}\n{2}", ex.GetType(), ex.Message, ex.StackTrace);
                }
            }

            public string GetIP() {
                IPAddress addr = TcpEndPoint.Address;
                return addr.MapToIPv4().ToString();
            }

            public void Close(bool sendClose, string reason = "") {
                if (Connected) {
                    Connected = false;
                    cts.Cancel();
                    if (_client != null) {
                        _client.Close();
                        _client = null;
                    }
                    if (_stream != null) {
                        _stream.Close();
                        _stream = null;
                    }
                    if (User != null)
                        User.Disconnected();
                    if (CloseMessage)
                        Logger.Log("{0}: closed {1}", SessionToken, reason != "" ? "- " + reason : "");
                    Instance.RemoveUdpID(UdpID);
                }
            }

            private void ProcessReceiveBuffer(byte[] buffer, Protocal type) {
                timeOutWatch.Reset();
                timeOutWatch.Start();

                if (buffer.Length > 0) {
                    byte command = buffer[0];
                    buffer = BufferUtils.RemoveFront(BufferUtils.Remove.CMD, buffer);
                    Data data = new Data(type, command, buffer);
                    Instance.Process(this, data);
                }
                else
                    Logger.Log("{1}: Received empty buffer!", type.ToString());
            }

            private byte[] AddLength(byte[] data) {
                byte[] lengthBuff = BitConverter.GetBytes((UInt16)(data.Length + 2));
                return BufferUtils.Add(lengthBuff, data);
            }
        }

        public static AsyncServer Instance { get; private set; }

        public int TcpPort { get; private set; }
        public int UdpPort { get; private set; }
        public bool Run { get; set; }

        private TcpListener _listener;
        private UdpClient _udpListener;
       
        private ConcurrentDictionary<string, SocketUser> _connectedUsers;
        private ConcurrentDictionary<byte, CMD> _commands;
        private ConcurrentDictionary<int, string> _udpUserMap;
        private Random rand;

        private CancellationTokenSource cts;
        private CancellationToken token;

        public AsyncServer(int tcpPort, int udpPort) {
            try {
                Instance = this;
                TcpPort = tcpPort;
                UdpPort = udpPort;
                Run = true;
                _connectedUsers = new ConcurrentDictionary<string, SocketUser>();
                _commands = new ConcurrentDictionary<byte, CMD>();
                _udpUserMap = new ConcurrentDictionary<int, string>();

                cts = new CancellationTokenSource();
                token = cts.Token;

                _listener = new TcpListener(IPAddress.Any, TcpPort);
                _listener.Start();

                _udpListener = new UdpClient(UdpPort);

                rand = new Random();

                Logger.Log("Starting server: tcp: {0}, udp: {1}", TcpPort, UdpPort);
                BeginUdpReceive();
                var _ = Listen();
            }
            catch(Exception ex) {
                Logger.LogError("{0}: {1}\n{2}", ex.GetType(), ex.Message, ex.StackTrace);
            }
        }

        private async Task Listen() {
            var client = default(TcpClient);

            while (!token.IsCancellationRequested) {
                try {
                    client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException) {
                    // The listener has been stopped.
                    Logger.Log("Listening stopped.");
                    return;
                }
                catch (Exception ex) {
                    Logger.LogError("{0}: {1}\n{2}", ex.GetType(), ex.Message, ex.StackTrace);
                }

                if (client == null)
                    return;
                var _ = Accept(client);
            }
        }

        private async Task Accept(TcpClient client) {
            try {
                SocketUser user = new SocketUser(this, client);
                byte result = 0x00;
                bool success = await user.HandleStartConnect();
                token.ThrowIfCancellationRequested();
                byte[] udpidBuff = new byte[0];
                if (success) {
                    AddPlayer(user.SessionToken, user);
                    result = 0x01;
                    ushort udpid = GetNewUdpID();
                    user.UdpID = udpid;
                    AddUdpID(udpid, user.SessionToken);
                    udpidBuff = BitConverter.GetBytes(udpid);
                    ServerBase.BaseInstance.UserConnected(user);
                }
                user.Send(0xff, BufferUtils.Add(new byte[] { 0x01, result }, udpidBuff));
                if (success)
                    await user.HandleMessages();
            }
            catch(Exception ex) {
                Logger.LogError("{0}: {1}\n{2}", ex.GetType(), ex.Message, ex.StackTrace);
            }
            finally {
                client.Close();
            }
        }

        public void BeginUdpReceive() {
            try {
                _udpListener.BeginReceive(new AsyncCallback(UdpReadCallback), null);
            }
            catch (SocketException) {}
            catch (Exception ex) {
                Logger.LogError("{0}: {1}\n{2}", ex.GetType(), ex.Message, ex.StackTrace);
            }
        }

        public void AddPlayer(string key, SocketUser instance) {
            if (!PlayerExists(key)) {
                _connectedUsers.TryAdd(key, instance);
            }
        }

        public void RemoveUser(string id) {
            if (PlayerExists(id)) {
                if (_connectedUsers[id].Connected) {
                    _connectedUsers[id].Close(true, "User Removed.");
                }
                SocketUser u;
                _connectedUsers.TryRemove(id, out u);
            }
        }

        public bool PlayerExists(string key) {
            return _connectedUsers.ContainsKey(key);
        }

        public SocketUser GetPlayer(string key) {
            if (PlayerExists(key))
                return _connectedUsers[key];
            return null;
        }

        public void AddCommands(object target) {
            MethodInfo[] methods = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            for (int i = 0; i < methods.Length; i++) {
                Command cmdAttribute = (Command)Attribute.GetCustomAttribute(methods[i], typeof(Command));
                if (cmdAttribute != null) {
                    CMD function = null;
                    if (methods[i].IsStatic)
                        function = (CMD)Delegate.CreateDelegate(typeof(CMD), methods[i]);
                    else
                        function = (CMD)Delegate.CreateDelegate(typeof(CMD), target, methods[i]);
                    if (function != null) {
                        if (CommandExists(cmdAttribute.command))
                            _commands[cmdAttribute.command] = function;
                        else
                            _commands.TryAdd(cmdAttribute.command, function);
                    }
                    else {
                        Logger.Log("{0} is null!", methods[i].Name);
                    }
                }
            }
        }

        public void AddCommand(byte cmd, CMD callback) {
            if (!CommandExists(cmd)) {
                _commands.TryAdd(cmd, callback);
            }
        }

        public void RemoveCommand(byte cmd) {
            if (CommandExists(cmd)) {
                CMD c;
                _commands.TryRemove(cmd, out c);
            }
        }

        public bool CommandExists(byte Cmd) {
            return _commands.ContainsKey(Cmd);
        }

        public void SendStoppedCommand() {
            string[] users = _connectedUsers.Keys.ToArray();
            Send(users, 0xff, new byte[] { 0x04 });
        }

        public void Send(string[] users, byte command, Protocal type = Protocal.Tcp) {
            Send(users, command, new byte[0], type);
        }

        public void Send(string[] users, byte command, string data, Protocal type = Protocal.Tcp) {
            Send(users, command, Encoding.UTF8.GetBytes(data), type);
        }

        public void Send(string[] users, byte command, byte[] data, Protocal type = Protocal.Tcp) {
            for (int i = 0; i < users.Length; i++) {
                Send(users[i], command, data, type);
            }
        }

        public void Send(string user, byte command, string data, Protocal type = Protocal.Tcp) {
            Send(user, command, Encoding.UTF8.GetBytes(data), type);
        }

        public void Send(string user, byte command, byte[] data, Protocal type = Protocal.Tcp) {
            if (PlayerExists(user)) {
                _connectedUsers[user].Send(command, data, type);
            }
        }

        public void Send(string user, byte[] data, Protocal type = Protocal.Tcp) {
            if (PlayerExists(user)) {
                _connectedUsers[user].Send(data, type);
            }
        }

        public void SendUdp(byte[] data, IPEndPoint endpoint) {
            _udpListener.BeginSend(data, data.Length, endpoint, new AsyncCallback(SendCallback), null);
        }

        public void Stop(bool sendStopped) {
            if (sendStopped)
                SendStoppedCommand();
            Run = false;
            cts.Cancel();
            foreach (SocketUser user in _connectedUsers.Values.ToArray()) {
                user.Close(true, "Server stopped");
            }
            _listener.Stop();
            _listener = null;
        }

        public IPAddress GetPublicIP() {
            IPHostEntry ipHostInfo = Dns.GetHostEntry(Dns.GetHostName());
            IPAddress addr = ipHostInfo.AddressList[0];
            return addr;
        }

        public IPAddress GetLocalIPAddress(bool useLocalhost) {
            if (useLocalhost)
                return IPAddress.Parse("127.0.0.1");

            var host = Dns.GetHostEntry(Dns.GetHostName());
            List<IPAddress> addresses = new List<IPAddress>();
            foreach (var ip in host.AddressList) {
                if (ip.AddressFamily == AddressFamily.InterNetwork) {
                    addresses.Add(ip);
                    Logger.Log(ip.ToString());
                }
            }
            return addresses[0];
            throw new Exception("Local IP Address Not Found!");
        }

        public void Process(SocketUser socketUser, Data data) {
            if (CommandExists(data.command)) {
                _commands[data.command](socketUser, data);
            }
            else
                Logger.LogError("No command: " + data.command);
        }

        private void SendCallback(IAsyncResult ar) {
            try {
                //Socket handler = (Socket)ar.AsyncState;
                //int bytesSent = handler.EndSend(ar);
                //Logger.Log("Sent {0} bytes to client.", bytesSent);

            }
            catch (Exception ex) {
                Logger.LogError("{0}: {1}\n{2}", ex.GetType(), ex.Message, ex.StackTrace);
            }
        }

        private void UdpReadCallback(IAsyncResult ar) {
            try {
                IPEndPoint senderEndpoint = new IPEndPoint(IPAddress.Any, 0);
                byte[] receivedBytes = _udpListener.EndReceive(ar, ref senderEndpoint);
                TaskQueue.QeueAsync("udp_calls", () => {
                    if (receivedBytes.Length > 0) {
                        ushort ID = BitConverter.ToUInt16(receivedBytes, 0);
                        receivedBytes = BufferUtils.RemoveFront(BufferUtils.Remove.UDP_ID, receivedBytes);
                        if (UdpIdExists(ID)) {
                            byte cmd = receivedBytes[0];
                            receivedBytes = BufferUtils.RemoveFront(BufferUtils.Remove.CMD, receivedBytes);
                            string key = _udpUserMap[ID];
                            if (PlayerExists(key)) {
                                SocketUser sUser = _connectedUsers[key];
                                sUser.UdpEndPoint = senderEndpoint;
                                Process(sUser, new Data(Protocal.Udp, cmd, receivedBytes));
                            }
                        }
                        else {
                            //Logger.LogError("Invalid UdpID from {0}: {1}", senderEndpoint.Address.ToString(), ID);
                        }
                    }
                });
            }
            catch (SocketException) {
                // nothing
            }
            BeginUdpReceive();
        }

        public ushort GetNewUdpID(int iteration = 0) {
            iteration++;
            ushort newID = (ushort)rand.Next(0, 65535);
            if (_udpUserMap.ContainsKey(newID))
                newID = GetNewUdpID(iteration);
            return newID;
        }

        public void AddUdpID(int udpID, string userID) {
            if (udpID <= 0)
                return;

            if (!_udpUserMap.ContainsKey(udpID)) {
                _udpUserMap.TryAdd(udpID, userID);
            }
        }

        private void RemoveUdpID(int udpID) {
            if (udpID <= 0)
                return;

            if (_udpUserMap.ContainsKey(udpID)) {
                string v;
                _udpUserMap.TryRemove(udpID, out v);
            }
        }

        private bool UdpIdExists(int udpID) {
            return _udpUserMap.ContainsKey(udpID);
        }
    }

    
}
