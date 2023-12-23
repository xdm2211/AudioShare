﻿using NAudio.Wave;
using SharpAdbClient;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using NamePair = System.Collections.Generic.KeyValuePair<AudioShare.AudioChannel, string>;

namespace AudioShare
{
    public class Speaker : INotifyPropertyChanged, IDisposable
    {
        enum Command
        {
            None = 0,
            AudioData = 1,
            Volume = 2,
            SampleRate = 3
        }
        public event PropertyChangedEventHandler PropertyChanged;
        public event EventHandler<Speaker> Remove;
        public event EventHandler<ConnectStatus> ConnectStatusChanged;
        private static readonly ObservableCollection<NamePair> _channels = new ObservableCollection<NamePair>();
        private static readonly byte[] TCP_HEAD = Encoding.Default.GetBytes("picapico-audio-share");
        private static readonly string REMOTE_SOCKET = "localabstract:picapico-audio-share";

        static Speaker()
        {
            _channels.Add(new NamePair(AudioChannel.Stereo, "立体声"));
            _channels.Add(new NamePair(AudioChannel.Left, "左声道"));
            _channels.Add(new NamePair(AudioChannel.Right, "右声道"));
            _channels.Add(new NamePair(AudioChannel.None, "禁用"));
        }
        private TcpClient tcpClient = null;
        readonly AdbClient adbClient = new AdbClient();
        private string _remoteIP = string.Empty;
        private int _remotePort = -1;
        private readonly bool _isUSB = false;
        private readonly string _name = string.Empty;
        private string _id = string.Empty;
        private readonly Dispatcher _dispatcher;

        public string Id => _id;

        public string Display
        {
            get => _isUSB ? $"{_name} [{_id}]" : _id;
            set { _id = value; }
        }
        public bool IdReadOnly => _isUSB || _connectStatus != ConnectStatus.UnConnected;
        public bool RemoveVisible => !_isUSB;
        public bool ChannelEnabled => _connectStatus == ConnectStatus.UnConnected;
        public bool ConnectEnabled => _channel != AudioChannel.None;
        private ConnectStatus _connectStatus = ConnectStatus.UnConnected;
        public bool Connected => _connectStatus == ConnectStatus.Connected;
        public bool Connecting => _connectStatus == ConnectStatus.Connecting;
        public bool UnConnected => _connectStatus != ConnectStatus.Connected;
        public ObservableCollection<NamePair> Channels => _channels;
        private AudioChannel _channel = AudioChannel.None;
        public NamePair ChannelSelected
        {
            get => _channels.FirstOrDefault(m => m.Key == _channel);
            set
            {
                _channel = value.Key;
                OnPropertyChanged(nameof(ConnectEnabled));
            }
        }

        public Speaker(Dispatcher dispatcher, string id, string name, AudioChannel channel, bool isUSB)
        {
            _id = id;
            _name = name;
            _channel = channel;
            _isUSB = isUSB;
            _dispatcher = dispatcher;
        }

        public Speaker(Dispatcher dispatcher, string id):this(dispatcher, id, AudioChannel.None)
        {
        }

        public Speaker(Dispatcher dispatcher, string id, AudioChannel channel): this(dispatcher, id, string.Empty, channel, false)
        {
        }

        public Speaker(Dispatcher dispatcher, string id, string name): this(dispatcher, id, name, AudioChannel.None)
        {
        }

        public Speaker(Dispatcher dispatcher, string id, string name, AudioChannel channel): this(dispatcher, id, name, channel, true)
        {
        }

        private void SetConnectStatus(ConnectStatus connectStatus)
        {
            if (_connectStatus == connectStatus) return;
            _dispatcher.InvokeAsync(() =>
            {
                _connectStatus = connectStatus;
                OnPropertyChanged(nameof(Connected));
                OnPropertyChanged(nameof(Connecting));
                OnPropertyChanged(nameof(UnConnected));
                OnPropertyChanged(nameof(IdReadOnly));
                OnPropertyChanged(nameof(RemoveVisible));
                OnPropertyChanged(nameof(ChannelEnabled));
                ConnectStatusChanged?.Invoke(this, connectStatus);
            });
        }

        public RelayCommand ConnectCommand => new RelayCommand(Connect, CanConnect);

        public RelayCommand DisConnectCommand => new RelayCommand(DisConnect, CanDisConnect);

        public RelayCommand RemoveCommand => new RelayCommand(RemoveSpeaker, CanRemoveSpeaker);

        public void SetVolume(int volume)
        {
            byte[] volumeBytes = BitConverter.GetBytes(volume);
            RequestTcp(Command.Volume, volumeBytes);
        }

        public void Dispose()
        {
            DisConnect(null);
        }

        private void Connect(object sender)
        {
            _ = Connect();
        }

        public async Task Connect()
        {
            if(Connecting) return;
            DisConnect();
            SetConnectStatus(ConnectStatus.Connecting);
            try
            {
                tcpClient = new TcpClient();
                tcpClient.NoDelay = true;
                if (_isUSB)
                {
                    if (!await EnsureDevice(_id))
                    {
                        throw new Exception("device not ready");
                    }
                    int port = await Utils.GetFreePort();
                    var device = adbClient.GetDevice(_id);
                    adbClient.RemoveRemoteForward(device, REMOTE_SOCKET);
                    adbClient.CreateForward(device, "tcp:" + port, REMOTE_SOCKET, true);
                    await tcpClient.ConnectAsync("127.0.0.1", port);
                    _remoteIP = "127.0.0.1";
                    _remotePort = port;
                }
                else
                {
                    var addressArr = _id.Split(':');
                    string ip = addressArr.FirstOrDefault()?.Trim() ?? string.Empty;
                    if (addressArr.Length < 2 ||
                        !int.TryParse(addressArr.LastOrDefault()?.Trim() ?? string.Empty, out int port))
                    {
                        port = 80;
                    }
                    if (!await EnsureDevice(ip, port))
                    {
                        throw new Exception("device not ready");
                    }
                    await tcpClient.ConnectAsync(ip, port);
                    _remoteIP = ip;
                    _remotePort = port;
                }
                if (tcpClient.Connected)
                {
                    _ = WriteTcp(TCP_HEAD);
                    _ = WriteTcp(new byte[] { (byte)Command.AudioData });
                    var sampleRateBytes = BitConverter.GetBytes(AudioManager.SampleRate);
                    _ = WriteTcp(sampleRateBytes);
                    var channelBytes = BitConverter.GetBytes(_channel == AudioChannel.Stereo ? 12 : 4);
                    _ = WriteTcp(channelBytes);
                    tcpClient.GetStream().Read(new byte[1], 0, 1);
                    tcpClient.SendBufferSize = 1024;
                    _ = _dispatcher.InvokeAsync(() =>
                    {
                        AudioManager.StartCapture();
                        switch (_channel)
                        {
                            case AudioChannel.Left:
                                AudioManager.LeftAvailable += SendAudioData;
                                break;
                            case AudioChannel.Right:
                                AudioManager.RightAvailable += SendAudioData;
                                break;
                            case AudioChannel.Stereo:
                                AudioManager.StereoAvailable += SendAudioData;
                                break;
                        }
                        AudioManager.Stoped += OnAudioStoped;
                    });
                }
                SetConnectStatus(ConnectStatus.Connected);
            }
            catch (Exception)
            {
                DisConnect();
            }
        }

        private void OnAudioStoped(object sender, EventArgs e)
        {
            DisConnect();
        }

        private async void SendAudioData(object sender, WaveInEventArgs e)
        {
            if (!(await WriteTcp(e.Buffer, e.BytesRecorded)))
            {
                DisConnect();
            }
        }

        private async Task<bool> EnsureDevice(string host, int port)
        {
            bool result = await Utils.PortIsOpen(host, port);
            string adbPath = Utils.FindAdbPath();
            if (string.IsNullOrWhiteSpace(adbPath)) return result;
            DeviceData device;
            bool needDisconnect = false;
            try
            {
                await Utils.EnsureAdb(adbPath);
                device = adbClient.GetDevices().FirstOrDefault(m => m.Serial?.StartsWith(host) ?? false);
                needDisconnect = device == null;
                if (device == null)
                {
                    if (!await Utils.PortIsOpen(host, 5555)) return result;
                    await Utils.RunCommandAsync(adbPath, $"connect {host}:5555");
                    device = adbClient.GetDevices().FirstOrDefault(m => m.Serial?.StartsWith(host) ?? false);
                }
                if (device == null) return result;
                result = await EnsureDevice(device);
            }
            catch (Exception)
            {
                return result;
            }
            if (needDisconnect) await Utils.RunCommandAsync(adbPath, $"disconnect {host}:5555");
            return result;
        }

        private Task<bool> EnsureDevice(string serial)
        {
            return EnsureDevice(adbClient.GetDevices().FirstOrDefault(m => m.Serial == serial));
        }

        private async Task<bool> EnsureDevice(DeviceData device)
        {
            if (device == null) return false;

            if (string.IsNullOrWhiteSpace(device.Serial)) return false;

            string result = await Utils.RunAdbShellCommandAsync(adbClient, "dumpsys package com.picapico.audioshare|grep versionName", device);
            if (result.Contains(Utils.VersionName))
            {
                await Utils.RunAdbShellCommandAsync(adbClient, "am start -W -n com.picapico.audioshare/.MainActivity", device);
                return true;
            }
            string appPath = Process.GetCurrentProcess()?.MainModule.FileName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(appPath)) return false;
            string apkPath = Path.Combine(Path.GetDirectoryName(appPath), Path.GetFileNameWithoutExtension(appPath) + ".apk");
            if (!File.Exists(apkPath))
            {
                MessageBox.Show(Application.Current.MainWindow, Languages.Language.GetLanguageText("apkMisMatch") +
                    Languages.Language.GetLanguageText("or") +
                    Languages.Language.GetLanguageText("apkExistsTips"),
                    Application.Current.MainWindow.Title);
                return false;
            }

            await Utils.RunAdbShellCommandAsync(adbClient, "rm -f /data/local/tmp/audioshare.apk", device);
            await adbClient.PushAsync(device, apkPath, "/data/local/tmp/audioshare.apk");
            await Utils.RunAdbShellCommandAsync(adbClient, "/system/bin/pm install -r /data/local/tmp/audioshare.apk && rm -f /data/local/tmp/audioshare.apk", device);

            result = await Utils.RunAdbShellCommandAsync(adbClient, "dumpsys package com.picapico.audioshare|grep versionName", device);
            if (result.Contains(Utils.VersionName))
            {
                await Utils.RunAdbShellCommandAsync(adbClient, "am start -W -n com.picapico.audioshare/.MainActivity", device);
                return true;
            }
            MessageBox.Show(Application.Current.MainWindow, 
                Languages.Language.GetLanguageText("apkMisMatch"),
                Application.Current.MainWindow.Title);
            return false;
        }

        private void DisConnect()
        {
            DisConnect(null);
        }

        private void DisConnect(object sender)
        {
            SetConnectStatus(ConnectStatus.UnConnected);
            _remoteIP = string.Empty;
            _remotePort = -1;
            try
            {
                AudioManager.Stoped -= OnAudioStoped;
            }
            catch (Exception)
            {
            }
            try
            {
                AudioManager.LeftAvailable -= SendAudioData;
            }
            catch (Exception)
            {
            }
            try
            {
                AudioManager.RightAvailable -= SendAudioData;
            }
            catch (Exception)
            {
            }
            try
            {
                AudioManager.StereoAvailable -= SendAudioData;
            }
            catch (Exception)
            {
            }
            try
            {
                tcpClient?.Close();
            }
            catch (Exception ex)
            {
                Trace.WriteLine("Stop Tcp Error: " + ex.Message);
            }
            tcpClient = null;
        }

        private void RemoveSpeaker(object sender)
        {
            Remove?.Invoke(null, this);
        }

        private bool CanConnect(object sender)
        {
            return true;
        }
        private bool CanDisConnect(object sender)
        {
            return true;
        }
        private bool CanRemoveSpeaker(object sender)
        {
            return _connectStatus == ConnectStatus.UnConnected;
        }

        private async Task<bool> WriteTcp(byte[] buffer, int length = 0)
        {
            if (length == 0) length = buffer.Length;
            if (length == 0) return true;
            try
            {
                if (tcpClient != null)
                {
                    await tcpClient.GetStream().WriteAsync(buffer, 0, length);
                    await tcpClient.GetStream().FlushAsync();
                    if(length > tcpClient.SendBufferSize)
                    {
                        tcpClient.SendBufferSize = length;
                    }
                    return true;
                }
            }
            catch (Exception)
            {
            }
            return false;
        }

        private async void RequestTcp(Command command, byte[] data)
        {
            if (UnConnected || Connecting || string.IsNullOrWhiteSpace(_remoteIP) || _remotePort <= 0) return;
            TcpClient client = new TcpClient();
            try
            {
                await client.ConnectAsync(_remoteIP, _remotePort);
                client.GetStream().Write(TCP_HEAD, 0, TCP_HEAD.Length);
                client.GetStream().Write(new byte[] { (byte)command }, 0, 1);
                client.GetStream().Write(data, 0, data.Length);
                await client.GetStream().FlushAsync();
                await client.GetStream().ReadAsync(new byte[1], 0, 1);
            }
            catch (Exception)
            {
            }
            try
            {
                client.Close();
            }
            catch (Exception)
            {
            }
        }

        private void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
