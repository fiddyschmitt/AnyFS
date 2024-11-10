using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace libCommon.Streams
{
    public class StandardStreams : Stream
    {
        public StandardStreams(Stream readFrom, Stream writeTo)
        {
            SendTo = writeTo;
            ReadFrom = readFrom;

            StartSendThread();
            StartReceiveThread();
        }

        Thread? sendThread;
        Thread? receiveThread;

        private void StartSendThread()
        {
            sendThread = new Thread(() =>
            {
                try
                {
                    foreach (var toSend in SendQueue.GetConsumingEnumerable(cancellationTokenSource.Token))
                    {
                        SendTo.Write(toSend);
                        SendTo.Flush();
                    }
                }
                catch (Exception ex)
                {
                    //Console.WriteLine($"{nameof(StartSendThread)} exception: {ex}");
                }
            })
            {
                IsBackground = true
            };


            sendThread.Start();
        }

        private void StartReceiveThread()
        {
            receiveThread = new Thread(async () =>
            {
                try
                {
                    var buffer = new byte[4094];

                    while (!cancellationTokenSource.IsCancellationRequested)
                    {
                        //var timeout = new CancellationTokenSource(5000);
                        var read = await ReadFrom.ReadAsync(buffer, cancellationTokenSource.Token);
                        var readBytes = new byte[read];
                        Array.Copy(buffer, readBytes, readBytes.Length);
                        ReceiveQueue.Add(readBytes);
                    }
                }
                catch (Exception ex)
                {
                    //Console.WriteLine($"{nameof(StartReceiveThread)} exception: {ex}");
                }
            })
            {
                IsBackground = true
            };

            receiveThread.Start();
        }

        public Stream SendTo { get; }
        public Stream ReadFrom { get; }

        readonly BlockingCollection<byte[]> ReceiveQueue = [];
        byte[]? currentReadBlock = null;
        int posInCurrentReadBlock = 0;
        CancellationTokenSource cancellationTokenSource = new();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var totalRead = 0;

            while (true)
            {
                var toRead = count - totalRead;
                if (toRead == 0) break;

                if (currentReadBlock == null || currentReadBlock.Length - posInCurrentReadBlock == 0)
                {
                    currentReadBlock = ReceiveQueue.GetConsumingEnumerable(cancellationTokenSource.Token).First();
                    posInCurrentReadBlock = 0;
                }

                var leftInBlock = currentReadBlock.Length - posInCurrentReadBlock;
                var toReadFromBlock = Math.Min(toRead, leftInBlock);
                Array.Copy(currentReadBlock, posInCurrentReadBlock, buffer, totalRead, toReadFromBlock);

                posInCurrentReadBlock += toReadFromBlock;
                totalRead += toReadFromBlock;
            }

            return totalRead;
        }

        readonly BlockingCollection<byte[]> SendQueue = [];
        public override void Write(byte[] buffer, int offset, int count)
        {
            var toSend = new byte[count];
            Array.Copy(buffer, offset, toSend, 0, count);
            SendQueue.Add(toSend);
        }

        public void Stop()
        {
            cancellationTokenSource.Cancel();
            receiveThread?.Join();
            sendThread.Join();
        }

        public override bool CanRead => throw new NotImplementedException();

        public override bool CanSeek => throw new NotImplementedException();

        public override bool CanWrite => throw new NotImplementedException();

        public override long Length => throw new NotImplementedException();

        public override long Position { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

        public override void Flush() => throw new NotImplementedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotImplementedException();

        public override void SetLength(long value) => throw new NotImplementedException();
    }
}
