using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using WootingAnalogSDKNET;
using NeatInput.Windows;
using NeatInput.Windows.Events;
using System.Globalization;
using SharpDX.XInput;
using System.Diagnostics;

namespace Woot_verlay
{
    internal static class Program
    {
        private static bool runSystem = true;
        private static bool runNonwooting = false;
        private static bool openToLan;
        private static List<TcpClient> activeConnections = new List<TcpClient>();

        [STAThread]
        static void Main()
        {
            ApplicationConfiguration.Initialize();
            Application.EnableVisualStyles();
            Application.SetHighDpiMode(HighDpiMode.DpiUnaware);
            Application.SetCompatibleTextRenderingDefault(false);
            SetCulture(CultureInfo.CurrentCulture.Name);

            var (numDevices, error) = WootingAnalogSDK.Initialise();

            var configForm = new SetupForm(numDevices < 0);
            if (configForm.ShowDialog() != DialogResult.OK)
            {
                Environment.Exit(1);
            }

            runNonwooting = configForm.UseNonWooting;
            openToLan = configForm.EnableLanMode;
            string ip = openToLan ? "0.0.0.0" : "127.0.0.1";

            if (configForm.EnableLanMode)
            {
                MessageBox.Show(Properties.Resources.ConfigForm_LanInfo, "Info", MessageBoxButtons.OK);
            }

            int port = 32312;
            var server = new TcpListener(IPAddress.Parse(ip), port);
            try
            {
                server.Start();
            }
            catch (Exception)
            {
                MessageBox.Show(Properties.Resources.ConfigForm_ServerError, "Woot-verlay - already running error");
                Environment.Exit(1);
            }

            var keyboardReceiver = new KeyListener();
            var inputSource = new InputSource(keyboardReceiver);
            inputSource.Listen();

            ThreadPool.QueueUserWorkItem(o => handleClients(server));
            ThreadPool.QueueUserWorkItem(o => runLoop(keyboardReceiver));

            Application.Run(new WootTrayApp());
        }

        private static void handleClients(TcpListener server)
        {
            while (runSystem)
            {
                TcpClient client = server.AcceptTcpClient();
                NetworkStream stream = client.GetStream();
                bool hasHandshaked = false;

                while (!hasHandshaked && client.Connected)
                {
                    byte[] bytes = new byte[client.Available];
                    stream.Read(bytes, 0, client.Available);
                    string s = Encoding.UTF8.GetString(bytes);

                    if (Regex.IsMatch(s, "^GET", RegexOptions.IgnoreCase))
                    {
                        string swk = Regex.Match(s, "Sec-WebSocket-Key: (.*)").Groups[1].Value.Trim();
                        string swka = swk + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
                        byte[] swkaSha1 = System.Security.Cryptography.SHA1.Create().ComputeHash(Encoding.UTF8.GetBytes(swka));
                        string swkaSha1Base64 = Convert.ToBase64String(swkaSha1);

                        byte[] response = Encoding.UTF8.GetBytes(
                            "HTTP/1.1 101 Switching Protocols\r\n" +
                            "Connection: Upgrade\r\n" +
                            "Upgrade: websocket\r\n" +
                            "Sec-WebSocket-Accept: " + swkaSha1Base64 + "\r\n\r\n");

                        stream.Write(response, 0, response.Length);
                        hasHandshaked = true;
                        activeConnections.Add(client);
                        Debug.WriteLine("Client connected, there are now " + activeConnections.Count + " connection/s.");
                    }
                }
            }
        }

        private static string lastGesture = null;
        private static int gestureFrameCount = 0;

        private static void runLoop(KeyListener keyboardReceiver)
        {
            var controller = new Controller(UserIndex.One);
            HashSet<int> controllerKeys = new HashSet<int>();
            StringBuilder contentBuilder = new StringBuilder();
            bool shownEmpty = false;

            while (runSystem)
            {
                List<TcpClient> disconnected = new List<TcpClient>();
                contentBuilder.Clear();

                var (keys, readErr) = WootingAnalogSDK.ReadFullBuffer(20);
                if (readErr == WootingAnalogResult.Ok)
                {
                    if (keys.Count > 0)
                    {
                        foreach (var analog in keys)
                        {
                            var pressed = keyboardReceiver.activeKeys.Contains(analog.Item1) ? 1 : 0;
                            contentBuilder.Append($"({analog.Item1}:{analog.Item2}:{pressed})");
                        }

                        var gesture = DetectGesture(keys);
                        if (gesture != null)
                        {
                            contentBuilder.Append($"|GESTURE:{gesture}");
                        }

                        shownEmpty = false;
                    }
                }

                if (contentBuilder.Length > 0 || !shownEmpty)
                {
                    activeConnections.ForEach(curClient =>
                    {
                        if (!sendMessage(curClient, contentBuilder.ToString()))
                        {
                            disconnected.Add(curClient);
                        }
                    });
                    if (contentBuilder.Length == 0) shownEmpty = true;
                }

                if (disconnected.Count > 0)
                {
                    disconnected.ForEach(client => activeConnections.Remove(client));
                    Debug.WriteLine(disconnected.Count + " client/s disconnected. " + activeConnections.Count + " connections remaining.");
                }

                Thread.Sleep(10);
            }
        }

        private static string DetectGesture(List<(short, float)> keys)
        {
            var strongKeys = keys.Where(k => k.Item2 >= 0.2f).Select(k => (int)k.Item1).ToList();

            if (strongKeys.Count == 0)
            {
                lastGesture = null;
                gestureFrameCount = 0;
                return null;
            }

            var rows = strongKeys.Select(k => k / 10).ToList();
            var cols = strongKeys.Select(k => k % 10).ToList();

            int rowRange = rows.Max() - rows.Min() + 1;
            int colRange = cols.Max() - cols.Min() + 1;

            string currentGesture = null;

            if (IsPalmGesture(strongKeys.Count, rowRange, colRange))
                currentGesture = "PALM";
            else if (IsFistGesture(strongKeys.Count, rowRange, colRange))
                currentGesture = "FIST";
            else if (IsSideGesture(strongKeys.Count, cols))
                currentGesture = "SIDE";

            if (currentGesture != null)
            {
                if (currentGesture == lastGesture)
                {
                    gestureFrameCount++;
                    if (gestureFrameCount >= 4)
                        return currentGesture;
                }
                else
                {
                    lastGesture = currentGesture;
                    gestureFrameCount = 1;
                }
            }
            else
            {
                lastGesture = null;
                gestureFrameCount = 0;
            }

            return null;
        }

        private static bool IsPalmGesture(int count, int rowRange, int colRange) =>
            count >= 8 && rowRange >= 2 && colRange >= 4;

        private static bool IsFistGesture(int count, int rowRange, int colRange) =>
            count >= 4 && count <= 6 && rowRange <= 2 && colRange <= 2;

        private static bool IsSideGesture(int count, List<int> cols)
        {
            var uniqueCols = cols.Distinct().OrderBy(c => c).ToList();
            bool adjacent = uniqueCols.Count <= 2 && (uniqueCols.Last() - uniqueCols.First() <= 1);
            return count >= 4 && count <= 5 && adjacent;
        }

        private static bool sendMessage(TcpClient client, string inputText)
        {
            if (!client.Connected) return false;
            NetworkStream stream = client.GetStream();

            byte[] sendBytes = Encoding.UTF8.GetBytes(inputText);
            byte lengthHeader = 0;
            byte[] lengthCount = new byte[] { };

            if (sendBytes.Length <= 125)
                lengthHeader = (byte)sendBytes.Length;
            if (125 < sendBytes.Length && sendBytes.Length < 65535)
            {
                lengthHeader = 126;
                lengthCount = new byte[] {
                    (byte)(sendBytes.Length >> 8),
                    (byte)(sendBytes.Length)
                };
            }
            if (sendBytes.Length > 65535)
            {
                lengthHeader = 127;
                lengthCount = new byte[] {
                    (byte)(sendBytes.Length >> 56),
                    (byte)(sendBytes.Length >> 48),
                    (byte)(sendBytes.Length >> 40),
                    (byte)(sendBytes.Length >> 32),
                    (byte)(sendBytes.Length >> 24),
                    (byte)(sendBytes.Length >> 16),
                    (byte)(sendBytes.Length >> 8),
                    (byte)sendBytes.Length,
                };
            }

            List<byte> responseArray = new List<byte>() { 0b10000001 };
            responseArray.Add(lengthHeader);
            responseArray.AddRange(lengthCount);
            responseArray.AddRange(sendBytes);

            try
            {
                stream.Write(responseArray.ToArray(), 0, responseArray.Count);
            }
            catch (Exception) { return false; };

            return true;
        }

        private static void SetCulture(string cultureCode)
        {
            Thread.CurrentThread.CurrentUICulture = new CultureInfo(cultureCode);
        }

        internal class KeyListener : IKeyboardEventReceiver
        {
            public List<int> activeKeys = new List<int>();
            public List<int> inActiveKeys = new List<int>();

            public void Receive(KeyboardEvent @event)
            {
                Enum.TryParse(@event.Key.ToString(), out keyMaps keyCode);

                if ((int)keyCode > 0)
                {
                    if (@event.State == NeatInput.Windows.Processing.Keyboard.Enums.KeyStates.Up && activeKeys.Contains((int)keyCode))
                    {
                        activeKeys.Remove((int)keyCode);
                        inActiveKeys.Add((int)keyCode);
                    }
                    else if (!activeKeys.Contains((int)keyCode))
                    {
                        activeKeys.Add((int)keyCode);
                    }
                }
            }

            public enum keyMaps
            {
                A = 4, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
                D1, D2, D3, D4, D5, D6, D7, D8, D9, D0,
                Return, Escape, Back, Tab, Space
            }
        }

        internal class WootTrayApp : ApplicationContext
        {
            private NotifyIcon trayIcon;

            public WootTrayApp()
            {
                SetCulture(CultureInfo.CurrentCulture.Name);

                string ipMessage = openToLan
                ? string.Format(Properties.Resources.Tray_LanIP, GetLocalIPAddress())
                : Properties.Resources.Tray_LocalMode;

                var strip = new ContextMenuStrip()
                {
                    Items =
                    {
                        new ToolStripMenuItem(ipMessage, null, null, ""),
                        new ToolStripMenuItem(Properties.Resources.Tray_StopOverlay, null, new EventHandler(Exit), "EXIT")
                    }
                };

                trayIcon = new NotifyIcon()
                {
                    Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath),
                    ContextMenuStrip = strip,
                    Visible = true
                };
            }

            void Exit(object? sender, EventArgs e)
            {
                trayIcon.Visible = false;
                Application.Exit();
            }
        }

        private static string GetLocalIPAddress()
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    return ip.ToString();
                }
            }
            throw new Exception("No IPv4 address found!");
        }
    }
}
