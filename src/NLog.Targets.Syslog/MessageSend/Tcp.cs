// Licensed under the BSD license
// See the LICENSE file in the project root for more information

using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NLog.Targets.Syslog.Extensions;
using NLog.Targets.Syslog.Settings;

namespace NLog.Targets.Syslog.MessageSend
{
    internal class Tcp : MessageTransmitter
    {
        private static readonly byte[] LineFeedBytes = { 0x0A };

        private readonly int connectionCheckTimeout;
        private readonly KeepAlive keepAlive;
        private readonly bool useTls;
        private readonly Func<X509Certificate2Collection> retrieveClientCertificates;
        private readonly int dataChunkSize;
        private readonly FramingMethod framing;
        private TcpClient tcp;
        private Stream stream;

        protected override bool Ready
        {
            get { return tcp?.Connected == true && tcp.Client.IsConnected(connectionCheckTimeout) == true; }
        }

        public Tcp(TcpConfig tcpConfig) : base(tcpConfig.Server, tcpConfig.Port, tcpConfig.ReconnectInterval)
        {
            connectionCheckTimeout = tcpConfig.ConnectionCheckTimeout;
            keepAlive = new KeepAlive(tcpConfig.KeepAlive);
            useTls = tcpConfig.Tls.Enabled;
            retrieveClientCertificates = tcpConfig.Tls.RetrieveClientCertificates;
            framing = tcpConfig.Framing;
            dataChunkSize = tcpConfig.DataChunkSize;
        }

        protected override Task Setup()
        {
            TearDown();

            tcp = new TcpClient();
            tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ExclusiveAddressUse, true);
            tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.DontLinger, false);
            tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Linger, new LingerOption(true, 0));
            // Call WSAIoctl via IOControl
            tcp.Client.IOControl(IOControlCode.KeepAliveValues, keepAlive.ToByteArray(), null);

            return tcp
                .ConnectAsync(IpAddress, Port)
                .Then(_ => stream = SslDecorate(tcp), CancellationToken.None);
        }

        protected override Task SendAsync(ByteArray message, CancellationToken token)
        {
            if (token.IsCancellationRequested)
                return Task.FromResult<object>(null);

            return FramingTask(message)
                .Then(_ => WriteAsync(0, message, token), token)
                .Unwrap();
        }

        protected override void TearDown()
        {
            DisposeSslStreamNotTcpClientInnerStream();
            DisposeTcpClientAndItsInnerStream();
        }

        private Stream SslDecorate(TcpClient tcpClient)
        {
            var tcpStream = tcpClient.GetStream();

            if (!useTls)
                return tcpStream;

            // Do not dispose TcpClient inner stream when disposing SslStream (TcpClient disposes it)
            var sslStream = new SslStream(tcpStream, true);
            sslStream.AuthenticateAsClient(Server, retrieveClientCertificates(), SslProtocols.Tls12, false);

            return sslStream;
        }

        private Task FramingTask(ByteArray message)
        {
            if (framing == FramingMethod.NonTransparent)
            {
                message.Append(LineFeedBytes);
                return Task.FromResult<object>(null);
            }

            var octetCount = message.Length;
            var prefix = new ASCIIEncoding().GetBytes($"{octetCount} ");
            return Task.Factory.SafeFromAsync(stream.BeginWrite, stream.EndWrite, prefix, 0, prefix.Length, null);
        }

        private Task WriteAsync(int offset, ByteArray data, CancellationToken token)
        {
            if (token.IsCancellationRequested)
                return Task.FromResult<object>(null);

            var toBeWrittenTotal = data.Length - offset;
            var isLastWrite = toBeWrittenTotal <= dataChunkSize;
            var count = isLastWrite ? toBeWrittenTotal : dataChunkSize;

            return Task.Factory
                .SafeFromAsync(stream.BeginWrite, stream.EndWrite, (byte[])data, offset, count, null)
                .Then(task => isLastWrite ? task : WriteAsync(offset + dataChunkSize, data, token), token)
                .Unwrap();
        }

        private void DisposeSslStreamNotTcpClientInnerStream()
        {
            if (useTls)
                stream?.Dispose();
        }

        private void DisposeTcpClientAndItsInnerStream()
        {
            tcp?.Close();
        }
    }
}