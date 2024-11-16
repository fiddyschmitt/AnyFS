using Newtonsoft.Json;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace libCommon
{
    public static class Extensions
    {
        public static string ToString(this IEnumerable<string> values, string separator)
        {
            var result = string.Join(separator, values);
            return result;
        }

        public static string ReadLine(this Stream stream)
        {
            var sb = new StringBuilder();

            while (true)
            {
                var ch = (char)stream.ReadByte();

                if (ch == '\n' || ch == '\r')
                {
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        stream.ReadByte();  //consume the LF
                    }
                    break;
                }
                sb.Append(ch);
            }

            var result = sb.ToString();
            return result;
        }

        public static long CopyTo(this Stream input, Stream output, long count, int bufferSize)
        {
            var buffer = new byte[bufferSize];
            var totalRead = 0L;

            //This has to be in a while loop, because the count that was requested could be larger than Array.MaxLength
            while (true)
            {
                if (count == 0) break;

                var toRead = (int)Math.Min(bufferSize, count);

                //var read = input.Read(buffer, 0, (int)Math.Min(bufferSize, count));
                input.ReadExactly(buffer, 0, toRead);
                var read = toRead;

                if (read == 0) break;
                totalRead += read;

                output.Write(buffer, 0, read);
                output.Flush();

                count -= read;
            }

            return totalRead;
        }

        public static string ToJson(this object? obj, bool indent = false)
        {
            var settings = new JsonSerializerSettings() { Formatting = Formatting.Indented };
            if (!indent)
            {
                settings = new JsonSerializerSettings() { Formatting = Formatting.None };
            }

            var result = JsonConvert.SerializeObject(obj, settings);
            return result;
        }

        public static string ToJson<T>(this object obj, bool indent = false)
        {
            var settings = new JsonSerializerSettings() { Formatting = Formatting.Indented };
            if (!indent)
            {
                settings = new JsonSerializerSettings() { Formatting = Formatting.None };
            }

            var result = JsonConvert.SerializeObject((T)obj, settings);
            return result;
        }

        //public static T? FromJson<T>(this string json)
        //{
        //    return JsonConvert.DeserializeObject<T>(json);
        //}

        public static T? FromJson<T>(this string json)
        {
            T myObj = default(T);
            var settings = new JsonSerializerSettings() { Formatting = Formatting.Indented };
            myObj = JsonConvert.DeserializeObject<T>(json, settings);
            return myObj;
        }

        public static IEnumerable<T> Recurse<T>(this T source, Func<T, T?> childSelector, bool depthFirst = false)
        {
            var list = new List<T>() { source };
            var childListSelector = new Func<T, IEnumerable<T>>(item =>
            {
                var child = childSelector(item);
                if (child == null)
                {
                    return new List<T>();
                }
                else
                {
                    return new List<T>() { child };
                }
            });

            foreach (var result in RecurseList(list, childListSelector, depthFirst))
            {
                yield return result;
            }
        }

        public static IEnumerable<T> RecurseList<T>(this IEnumerable<T> source, Func<T, IEnumerable<T>> childSelector, bool depthFirst = false)
        {
            List<T> queue = new(source);

            while (queue.Count > 0)
            {
                var item = queue[0];
                queue.RemoveAt(0);

                var children = childSelector(item);

                if (depthFirst)
                {
                    queue.InsertRange(0, children);
                }
                else
                {
                    queue.AddRange(children);
                }

                yield return item;
            }
        }
    }
}
