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
                        var read = await ReadFrom.ReadAsync(buffer, cancellationTokenSource.Token);
                        if (read > 0)
                        {
                            var readBytes = new byte[read];
                            Array.Copy(buffer, readBytes, readBytes.Length);
                            ReceiveQueue.Add(readBytes);
                        }
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
            lock (this)
            {
                if (count == 0) return 0;

                if (currentReadBlock == null || posInCurrentReadBlock >= currentReadBlock.Length)
                {
                    try
                    {
                        currentReadBlock = ReceiveQueue.GetConsumingEnumerable(cancellationTokenSource.Token).First();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(ex);
                    }
                    posInCurrentReadBlock = 0;
                }

                var leftInBlock = currentReadBlock.Length - posInCurrentReadBlock;
                var toReadFromBlock = Math.Min(count, leftInBlock);
                Array.Copy(currentReadBlock, posInCurrentReadBlock, buffer, offset, toReadFromBlock);

                posInCurrentReadBlock += toReadFromBlock;

                return toReadFromBlock;
            }
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

        public override bool CanRead => ReadFrom != null;

        public override bool CanSeek => throw new NotImplementedException();

        public override bool CanWrite => throw new NotImplementedException();

        public override long Length => throw new NotImplementedException();

        public override long Position { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

        public override void Flush() => throw new NotImplementedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotImplementedException();

        public override void SetLength(long value) => throw new NotImplementedException();
    }
}
