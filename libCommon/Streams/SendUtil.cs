using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libCommon.Streams
{
    public class SendUtil
    {
        public SendUtil(StreamWriter streamWriter)
        {
            StreamWriter = streamWriter;
            StartWriteThread();
        }

        private void StartWriteThread()
        {
            var t = new Thread(() =>
            {
                foreach (var str in toSend.GetConsumingEnumerable())
                {
                    StreamWriter.WriteLine(str);
                }
            })
            {
                IsBackground = true
            };

            t.Start();
        }

        BlockingCollection<string> toSend = [];

        public StreamWriter StreamWriter { get; }

        public void WriteLine(string str)
        {
            toSend.Add(str);
        }
    }
}
