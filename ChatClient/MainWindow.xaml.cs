using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using System.Collections.Generic; // <== penting untuk HashSet

namespace ChatClient
{
    public partial class MainWindow : Window
    {
        record ChatMessage(string type, string? from, string? to, string? text, long ts);

        TcpClient? _tcp;
        NetworkStream? _stream;
        StreamReader? _reader;
        readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        bool _running;

        // Typing Indicator (Ijun)
        string _username = "";
        readonly HashSet<string> _typingUsers = new(StringComparer.OrdinalIgnoreCase);
        readonly DispatcherTimer _typingUiTimer = new() { Interval = TimeSpan.FromSeconds(2) };

        // Theme Toggle (Reza)
        private bool _isDark = false;

        // Debounce pengiriman typing/stoptyping (lokal)
        bool _isTypingLocal = false;
        readonly DispatcherTimer _typingSendTimer = new() { Interval = TimeSpan.FromMilliseconds(1200) };

        public MainWindow()
        {
            InitializeComponent();

            // Auto-hide indikator (UI) dan kirim stoptyping jika perlu
            _typingUiTimer.Tick += async (_, __) =>
            {
                _typingUiTimer.Stop();

                // Sembunyikan bar
                TypingStatusBar.Visibility = Visibility.Collapsed;
                // (teks dibiarkan; akan ditimpa saat muncul lagi)

                // Jika masih dianggap mengetik secara lokal, kirim stop
                _typingSendTimer.Stop();
                if (_isTypingLocal)
                {
                    _isTypingLocal = false;
                    if (!string.IsNullOrWhiteSpace(_username))
                    {
                        var stopMsg = new ChatMessage("stoptyping", _username, null, null, NowTs());
                        await SendAsync(stopMsg);
                    }
                }
            };

            // Timer khusus untuk debounce event typing lokal
            _typingSendTimer.Tick += async (_, __) =>
            {
                _typingSendTimer.Stop();
                if (_isTypingLocal)
                {
                    _isTypingLocal = false;
                    if (!string.IsNullOrWhiteSpace(_username))
                    {
                        var stopMsg = new ChatMessage("stoptyping", _username, null, null, NowTs());
                        await SendAsync(stopMsg);
                    }
                }
            };

            // Default: Light Theme
            ApplyLightTheme();
        }

        // === THEME LOGIC ===
        private void ApplyLightTheme()
        {
            Resources["WindowBackground"] = new SolidColorBrush(Colors.White);
            Resources["InputBackground"] = new SolidColorBrush(Colors.White);
            Resources["ButtonBackground"] = new SolidColorBrush(Colors.LightGray);
            Resources["ButtonDisabledBackground"] = new SolidColorBrush(Colors.Gainsboro);
            Resources["TextBrush"] = new SolidColorBrush(Colors.Black);
            Resources["TextDisabledBrush"] = new SolidColorBrush(Colors.DarkGray);

            // Typing Indicator (light)
            Resources["TypingBackground"] = new SolidColorBrush(Colors.WhiteSmoke);
            Resources["TypingForeground"] = new SolidColorBrush(Colors.Black);

            BtnToggleTheme.Content = "Toggle Dark Mode";
        }

        private void ApplyDarkTheme()
        {
            var darkGrey = (Color)ColorConverter.ConvertFromString("#2D2D2D");
            var darkerGrey = (Color)ColorConverter.ConvertFromString("#1E1E1E");

            Resources["WindowBackground"] = new SolidColorBrush(darkGrey);
            Resources["InputBackground"] = new SolidColorBrush(darkerGrey);
            Resources["ButtonBackground"] = new SolidColorBrush(darkGrey);
            Resources["ButtonDisabledBackground"] = new SolidColorBrush(Color.FromRgb(100, 100, 100));
            Resources["TextBrush"] = new SolidColorBrush(Colors.White);
            Resources["TextDisabledBrush"] = new SolidColorBrush(Colors.Black); // kontras di dark

            // Typing Indicator (dark)
            Resources["TypingBackground"] = new SolidColorBrush(Color.FromRgb(60, 60, 60)); // dark grey
            Resources["TypingForeground"] = new SolidColorBrush(Colors.White);

            BtnToggleTheme.Content = "Toggle Light Mode";
        }

        private void ToggleTheme_Click(object sender, RoutedEventArgs e)
        {
            if (_isDark) { ApplyLightTheme(); _isDark = false; }
            else { ApplyDarkTheme(); _isDark = true; }
        }

        // === Connect / Disconnect ===
        async void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            if (_running) return;

            try
            {
                _tcp = new TcpClient();
                await _tcp.ConnectAsync(TbIp.Text.Trim(), int.Parse(TbPort.Text.Trim()));
                _stream = _tcp.GetStream();
                _reader = new StreamReader(_stream, Encoding.UTF8);
                _running = true;

                BtnConnect.IsEnabled = false;
                BtnDisconnect.IsEnabled = true;

                AppendChat($"[local] connected to {TbIp.Text}:{TbPort.Text}");

                _username = TbUser.Text.Trim();
                var join = new ChatMessage("join", _username, null, null, NowTs());
                await SendAsync(join);

                _ = Task.Run(ReadLoop);
            }
            catch (Exception ex)
            {
                AppendChat("[error] " + ex.Message);
                await DisconnectAsync();
            }
        }

        async void BtnDisconnect_Click(object sender, RoutedEventArgs e) => await DisconnectAsync();

        async Task DisconnectAsync()
        {
            if (!_running) return;
            _running = false;

            try
            {
                if (_stream is not null)
                {
                    var leave = new ChatMessage("leave", TbUser.Text.Trim(), null, null, NowTs());
                    await SendAsync(leave);
                }
            }
            catch { }

            try { _reader?.Dispose(); } catch { }
            try { _stream?.Close(); } catch { }
            try { _tcp?.Close(); } catch { }

            _reader = null; _stream = null; _tcp = null;

            BtnConnect.IsEnabled = true;
            BtnDisconnect.IsEnabled = false;
            AppendChat("[local] disconnected");

            _typingUsers.Clear();
            RenderTypingStatusBar();
        }

        // === Read loop ===
        async Task ReadLoop()
        {
            try
            {
                while (_running && _reader is not null)
                {
                    var line = await _reader.ReadLineAsync();
                    if (line == null) break;
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    ChatMessage? msg = null;
                    try { msg = JsonSerializer.Deserialize<ChatMessage>(line, _json); } catch { }
                    if (msg is null) continue;

                    switch (msg.type)
                    {
                        case "typing":
                            if (!string.Equals(msg.from, _username, StringComparison.OrdinalIgnoreCase) &&
                                !string.IsNullOrWhiteSpace(msg.from))
                            {
                                _typingUsers.Add(msg.from);
                                RenderTypingStatusBar();
                            }
                            break;

                        case "stoptyping":
                            if (!string.Equals(msg.from, _username, StringComparison.OrdinalIgnoreCase) &&
                                !string.IsNullOrWhiteSpace(msg.from))
                            {
                                _typingUsers.Remove(msg.from);
                                RenderTypingStatusBar();
                            }
                            break;

                        case "sys": AppendChat($"[sys] {msg.text}"); break;
                        case "msg": AppendChat($"{msg.from}: {msg.text}"); break;
                        case "pm": AppendChat($"[pm] {msg.from} → {msg.to}: {msg.text}"); break;
                        case "userlist": UpdateUsers(msg.text ?? ""); break;
                    }
                }
            }
            catch (IOException) { }
            catch (Exception ex) { AppendChat("[error] " + ex.Message); }
            finally
            {
                await Dispatcher.InvokeAsync(async () => await DisconnectAsync());
            }
        }

        // === UI Updates ===
        void AppendChat(string line)
        {
            Dispatcher.Invoke(() =>
            {
                LbChat.Items.Add($"[{DateTime.Now:HH:mm:ss}] {line}");
                if (LbChat.Items.Count > 0)
                    LbChat.ScrollIntoView(LbChat.Items[^1]);

                if (_typingUsers.Count > 0)
                    RenderTypingStatusBar();
            });
        }

        void UpdateUsers(string csv)
        {
            Dispatcher.Invoke(() =>
            {
                LbUsers.Items.Clear();
                foreach (var u in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    LbUsers.Items.Add(u);
            });
        }

        void RenderTypingStatusBar()
        {
            Dispatcher.Invoke(() =>
            {
                if (_typingUsers.Count == 0)
                {
                    TypingStatusBar.Visibility = Visibility.Collapsed;
                    TypingStatusText.Text = "";
                    _typingUiTimer.Stop();
                    return;
                }

                string text = _typingUsers.Count == 1
                    ? $"{string.Join(", ", _typingUsers)} is typing…"
                    : $"{string.Join(", ", _typingUsers)} are typing…";

                TypingStatusText.Text = text;
                TypingStatusBar.Visibility = Visibility.Visible;

                // restart auto-hide timer
                _typingUiTimer.Stop();
                _typingUiTimer.Start();
            });
        }

        // === Input & Send ===
        async void BtnSend_Click(object sender, RoutedEventArgs e) => await SendFromInputAsync();

        async void TbInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { e.Handled = true; await SendFromInputAsync(); }
        }

        async Task SendFromInputAsync()
        {
            var text = TbInput.Text;
            if (string.IsNullOrWhiteSpace(text)) return;
            TbInput.Clear();

            if (text.StartsWith("/w ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = text.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3)
                {
                    var to = parts[1];
                    var body = parts[2];
                    var pm = new ChatMessage("pm", TbUser.Text.Trim(), to, body, NowTs());
                    await SendAsync(pm);
                }
                else
                {
                    AppendChat("[local] usage: /w <username> <message>");
                }
            }
            else
            {
                var msg = new ChatMessage("msg", TbUser.Text.Trim(), null, text, NowTs());
                await SendAsync(msg);
            }

            // Kirim stoptyping segera sesudah kirim pesan
            _typingSendTimer.Stop();
            if (_isTypingLocal)
            {
                _isTypingLocal = false;
                var stop = new ChatMessage("stoptyping", _username, null, null, NowTs());
                await SendAsync(stop);
            }
        }

        private async void TbInput_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (!_running) return;

            var text = TbInput.Text;

            // Kosong atau whisper: hentikan typing
            if (string.IsNullOrWhiteSpace(text) || text.StartsWith("/w"))
            {
                _typingSendTimer.Stop();
                if (_isTypingLocal)
                {
                    _isTypingLocal = false;
                    var stop = new ChatMessage("stoptyping", TbUser.Text.Trim(), null, null, NowTs());
                    await SendAsync(stop);
                }
                return;
            }

            // Ada teks normal: kalau sebelumnya idle -> kirim "typing" sekali
            if (!_isTypingLocal)
            {
                _isTypingLocal = true;
                var typing = new ChatMessage("typing", TbUser.Text.Trim(), null, null, NowTs());
                await SendAsync(typing);
            }

            // Debounce untuk auto-stop setelah user berhenti ngetik sejenak
            _typingSendTimer.Stop();
            _typingSendTimer.Start();
        }

        void LbUsers_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (LbUsers.SelectedItem is string u)
            {
                TbInput.Text = $"/w {u} ";
                TbInput.CaretIndex = TbInput.Text.Length;
                TbInput.Focus();
            }
        }

        async Task SendAsync(ChatMessage msg)
        {
            try
            {
                if (_stream is null) return;
                var json = JsonSerializer.Serialize(msg, _json) + "\n";
                var bytes = Encoding.UTF8.GetBytes(json);
                await _stream.WriteAsync(bytes);
            }
            catch (Exception ex) { AppendChat("[error] " + ex.Message); }
        }

        static long NowTs() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }
}
