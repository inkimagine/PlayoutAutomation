using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Serialization;

namespace Svt.Caspar
{
    public class CasparDevice: IDisposable
    {
        internal Svt.Network.ServerConnection Connection { get; private set; }
        private Svt.Network.ReconnectionHelper ReconnectionHelper { get; set; }
        private System.Net.IPAddress[] HostAddresses;
        public CasparDeviceSettings Settings { get; private set; }
        public Channel[] Channels { get; private set; }
        public Recorder[] Recorders { get; private set; }
        public TemplatesCollection Templates { get; private set; }
        public List<MediaInfo> Mediafiles { get; private set; }
        public List<string> Datafiles { get; private set; }

        public string Version { get; private set; }

        public bool IsConnected => Connection != null && Connection.IsConnected;
        public bool IsRecordingSupported { get; set; }

        [Obsolete("This event is obsolete. Use the new ConnectionStatusChanged instead")]
		public event EventHandler<Svt.Network.NetworkEventArgs> Connected;
        [Obsolete("This event is obsolete. Use the new ConnectionStatusChanged instead")]
        public event EventHandler<Svt.Network.NetworkEventArgs> Disconnected;

        public event EventHandler<Svt.Network.ConnectionEventArgs> ConnectionStatusChanged;

		public event EventHandler<DataEventArgs> DataRetrieved;
		public event EventHandler<EventArgs> UpdatedChannels;
        public event EventHandler<EventArgs> UpdatedRecorders;
        public event EventHandler<EventArgs> UpdatedTemplates;
		public event EventHandler<EventArgs> UpdatedMediafiles;
		public event EventHandler<EventArgs> UpdatedDatafiles;
        public event EventHandler<Network.Osc.OscPacketEventArgs> OscMessage;

        volatile bool bIsDisconnecting = false;

		public CasparDevice()
		{
            Settings = new CasparDeviceSettings();
            Connection = new Network.ServerConnection();
            Channels = new Channel[0];
            Recorders = new Recorder[0];
		    Templates = new TemplatesCollection();
		    Mediafiles = new List<MediaInfo>();
		    Datafiles = new List<string>();
            HostAddresses = new System.Net.IPAddress[0];

            Version = "unknown";

            Connection.ProtocolStrategy = new AMCP.AMCPProtocolStrategy(this);
            Connection.ConnectionStateChanged += server__ConnectionStateChanged;
		}

        bool _disposed;
        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                Disconnect();
            }
        }

        #region OSC

        private void OscListener_PacketReceived(Network.Osc.OscPacketEventArgs e)
        {
            if (HostAddresses.Any(a => a.Equals(e.SourceAddress)))
            {
                OscMessage?.Invoke(this, e);
                var message = e.Packet as Svt.Network.Osc.OscMessage;
                var recorders = Recorders;
                if (message != null)
                    OscMessageReceived(message);
                else
                {
                    var bundle = e.Packet as Svt.Network.Osc.OscBundle;
                    if (bundle != null)
                        foreach (var m in bundle.Messages)
                           OscMessageReceived(m);
                }
            }
        }

        private void OscMessageReceived(Network.Osc.OscMessage message)
        {
            string[] path = message.Address.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (path.Length >= 2)
            {
                int id;
                if (int.TryParse(path[1], out id))
                    switch (path[0])
                    {
                        case "channel":
                            var channels = Channels;
                            channels.FirstOrDefault(c => c.ID == id)?.OscMessage(path, message.Arguments);
                            break;
                        case "recorder":
                            var recorders = Recorders;
                            recorders?.FirstOrDefault(r => r.Id == id)?.OscMessage(path, message.Arguments);
                            break;
                    }
            }
        }


        #endregion OSC

            #region Server notifications
        private void server__ConnectionStateChanged(object sender, Network.ConnectionEventArgs e)
        {
            try
            {
                if (ConnectionStatusChanged != null)
                    ConnectionStatusChanged(this, e);
            }
            catch { }

            if (e.Connected)
            {
                Connection.SendString("VERSION");

                //Ask server for channels
                Connection.SendString("INFO SERVER");

                //Ask server for recorders
                if (IsRecordingSupported)
                    Connection.SendString("INFO RECORDERS");

                //For compability with legacy users
                try
                {
                    if (Connected != null)
                    {
                        Connection.SendString("TLS");
                        Connected(this, new Svt.Network.NetworkEventArgs(e.Hostname, e.Port));
                    }
                }
                catch { }
            }
            else
            {
                lock (this)
                {
                    try
                    {
                        if (!bIsDisconnecting && Settings.AutoConnect)
                        {
                            Connection.ConnectionStateChanged -= server__ConnectionStateChanged;
                            ReconnectionHelper = new Svt.Network.ReconnectionHelper(Connection, Settings.ReconnectInterval);
                            ReconnectionHelper.Reconnected += ReconnectionHelper_Reconnected;
                            ReconnectionHelper.Start();
                        }
                    }
                    catch { }
                    bIsDisconnecting = false;
                }

                //For compability with legacy users
                try
                {
                    Disconnected?.Invoke(this, new Svt.Network.NetworkEventArgs(e.Hostname, e.Port));
                }
                catch { }
            }
        }

        void ReconnectionHelper_Reconnected(object sender, Network.ConnectionEventArgs e)
        {
            lock (this)
            {
                ReconnectionHelper.Close();
                ReconnectionHelper = null;
                Connection.ConnectionStateChanged += server__ConnectionStateChanged;
            }
            server__ConnectionStateChanged(Connection, e);
        }
		#endregion

        public void SendString(string command)
        {
            if (IsConnected)
                Connection.SendString(command);
        }
		public void RefreshMediafiles()
		{
			if (IsConnected)
                Connection.SendString("CLS");
		}
		public void RefreshTemplates()
		{
			if (IsConnected)
                Connection.SendString("TLS");
		}
		public void RefreshDatalist()
		{
			if (IsConnected)
                Connection.SendString("DATA LIST");
		}
		public void StoreData(string name, ICGDataContainer data)
		{
            if (IsConnected)
                Connection.SendString(string.Format("DATA STORE \"{0}\" \"{1}\"", name, data.ToAMCPEscapedXml())); 
		}
		public void RetrieveData(string name)
		{
			if (IsConnected)
                Connection.SendString(string.Format("DATA RETRIEVE \"{0}\"", name));
		}

		#region Connection
        public bool Connect(string host, int port)
        {
            return Connect(host, port, 0, false);
        }

        public bool Connect(string host, int amcpPort, int oscPort, bool reconnect)
        {
            if (!IsConnected)
            {
                Settings.Hostname = host;
                Settings.AmcpPort = amcpPort;
                Settings.OscPort = oscPort;
                Settings.AutoConnect = reconnect;
                return Connect();
            }
            return false;
        }

        public bool Connect()
		{
            if (Settings.OscPort > 0)
            {
                HostAddresses = System.Net.Dns.GetHostAddresses(Settings.Hostname);
                Network.Osc.OscPacketDispatcher.Bind(Settings.OscPort, OscListener_PacketReceived);
            }
            if (!IsConnected)
			{
                Connection.InitiateConnection(Settings.Hostname, Settings.AmcpPort);
				return true;
			}
			return false;
		}

        public void Disconnect()
        {
            bIsDisconnecting = true;
            if (ReconnectionHelper != null)
            {
                ReconnectionHelper.Close();
                ReconnectionHelper = null;
                Connection.ConnectionStateChanged += server__ConnectionStateChanged;
            }
            Connection.CloseConnection();
            if (Settings.OscPort > 0)
                Network.Osc.OscPacketDispatcher.UnBind(Settings.OscPort, OscListener_PacketReceived);
        }
		#endregion
        
		#region AMCP-protocol callbacks
		internal void OnUpdatedChannelInfo(string channelsXml)
		{
            var serializer = new XmlSerializer(typeof(ChannelList));
            using (StringReader reader = new StringReader(channelsXml))
            {
                var newChannels = (ChannelList)serializer.Deserialize(reader);
                if (newChannels.Channels != null)
                    foreach (Channel channel in newChannels.Channels)
                        channel.Connection = this.Connection;
                Channels = newChannels.Channels ?? new Channel[0];

                UpdatedChannels?.Invoke(this, EventArgs.Empty);
            }
        }

        internal void OnUpdatedRecorderInfo(string recordersXml)
        {
            var serializer = new XmlSerializer(typeof(RecorderList));
            using (StringReader reader = new StringReader(recordersXml))
            {
                var recorders = (RecorderList)serializer.Deserialize(reader);
                if (recorders.Recorders != null)
                    foreach (Recorder recorder in recorders.Recorders)
                        recorder.Connection = this.Connection;
                Recorders = recorders.Recorders ?? new Recorder[0];
                UpdatedRecorders?.Invoke(this, EventArgs.Empty);
                Debug.WriteLine($"Updated {Connection.Hostname}");
            }
        }

        internal void OnUpdatedTemplatesList(List<TemplateInfo> templates)
		{
            TemplatesCollection newTemplates = new TemplatesCollection();
            newTemplates.Populate(templates);
            Templates = newTemplates;

            UpdatedTemplates?.Invoke(this, EventArgs.Empty);
        }

		internal void OnUpdatedMediafiles(List<MediaInfo> mediafiles)
		{
            Mediafiles = mediafiles;

            UpdatedMediafiles?.Invoke(this, EventArgs.Empty);
        }

		internal void OnVersion(string version)
		{
			Version = version;
		}

		internal void OnLoad(string clipname)
		{
		}

		internal void OnLoadBG(string clipname)
		{
		}

		internal void OnUpdatedDataList(List<string> datafiles)
		{
            Datafiles = datafiles;
            UpdatedDatafiles?.Invoke(this, EventArgs.Empty);
        }

		internal void OnDataRetrieved(string data)
		{
            DataRetrieved?.Invoke(this, new DataEventArgs(data));
        }
		#endregion
	}

	public class DataEventArgs : EventArgs
	{
		public DataEventArgs(string data)
		{
			Data = data;
		}

		public string Data { get; set; }
	}

	public class CasparDeviceSettings
	{
        public const int DefaultReconnectInterval = 5000;

        public CasparDeviceSettings()
        {
            ReconnectInterval = DefaultReconnectInterval;
        }

        public string Hostname { get; set; }
        public int AmcpPort { get; set; }
        public bool AutoConnect { get; set; }
        public int ReconnectInterval { get; set; }
        public int OscPort { get; set; }
    }
}
